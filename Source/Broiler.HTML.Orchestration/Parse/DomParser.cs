using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Broiler.HTML.Dom.Parse;
using Broiler.HTML.Dom;
using HtmlConstants = Broiler.HTML.Utils.HtmlConstants;
using Broiler.HTML.Utils;
using Broiler.HTML.Core.Entities;
using Broiler.HTML.Core;

using HtmlTag = Broiler.Layout.HtmlTag;
using BoxKind = Broiler.Layout.BoxKind;
using Broiler.CSS;
using CssConstants = Broiler.CSS.CssConstants;
using Broiler.Layout.Engine;
using Broiler.Graphics;
namespace Broiler.HTML.Orchestration.Parse;

internal sealed class DomParser
{
    private readonly IStylesheetLoader _stylesheetLoader;

    // HTML presentation attributes (cellspacing/cellpadding) are projected as
    // low-priority hints that, per the CSS cascade, outrank the UA origin but lose
    // to author/inline declarations. We record which CSS longhands each box took
    // from such a hint so the cascade projection can preserve them when the only
    // competing declaration is a user-agent rule (e.g. `td { padding: 1px }`).
    private readonly Dictionary<CssBox, HashSet<string>> _presentationalHints = [];

    // Author-origin-only cascade (stylesheets + inline), used to detect whether a
    // presentation-hint property is also claimed by an author declaration.
    private CSS.Dom.CssStyleEngine? _authorEngine;

    public DomParser(IStylesheetLoader stylesheetLoader)
    {
        ArgumentNullException.ThrowIfNull(stylesheetLoader);
        _stylesheetLoader = stylesheetLoader;
    }

    private void RecordPresentationalHint(CssBox box, params string[] cssLonghands)
    {
        if (!_presentationalHints.TryGetValue(box, out var set))
            _presentationalHints[box] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var longhand in cssLonghands)
            set.Add(longhand);
    }

    public CssBox GenerateCssTree(string html, HtmlContainerInt htmlContainer, ref HtmlStyleSet styleSet, Uri baseUrl)
    {
        CssBox root;
        using (Broiler.Layout.Diagnostics.RenderStageTrace.Measure(Broiler.Layout.Diagnostics.RenderStageTrace.SubStages.HtmlParse))
            root = HtmlParser.ParseDocument(html, baseUrl);
        return PrepareCssTree(root, htmlContainer, ref styleSet, baseUrl);
    }

    public CssBox GenerateCssTree(Broiler.Dom.DomDocument document, HtmlContainerInt htmlContainer, ref HtmlStyleSet styleSet, Uri baseUrl)
    {
        CssBox root;
        using (Broiler.Layout.Diagnostics.RenderStageTrace.Measure(Broiler.Layout.Diagnostics.RenderStageTrace.SubStages.HtmlParse))
            root = HtmlParser.ParseDocument(document, baseUrl, htmlContainer?.ContentDocumentResolver);
        return PrepareCssTree(root, htmlContainer, ref styleSet, baseUrl);
    }

    private CssBox PrepareCssTree(CssBox root, HtmlContainerInt htmlContainer, ref HtmlStyleSet styleSet, Uri baseUrl)
    {
        if (root == null)
            return root;

        root.ContainerInt = htmlContainer;
        // Bind the layout environment at construction so font/colour and the
        // initial-containing-block inputs resolve through it (roadmap §4, Phase 4 prep).
        root.LayoutEnvironment = new HtmlLayoutEnvironment(htmlContainer);

        using (Broiler.Layout.Diagnostics.RenderStageTrace.Measure(Broiler.Layout.Diagnostics.RenderStageTrace.SubStages.CssParse))
            CascadeParseStyles(root, ref styleSet);

        // Resolve every stylesheet, inline declaration, generated pseudo-element,
        // animation, and ::selection rule through the shared model and style engine.
        var viewport = htmlContainer?.ViewportSize ?? default;
        var canonicalDocument = SharedRendererCascade.FindCanonicalDocument(root);
        Broiler.CSS.Dom.CssStyleEngine engine;
        using (Broiler.Layout.Diagnostics.RenderStageTrace.Measure(Broiler.Layout.Diagnostics.RenderStageTrace.SubStages.CascadeResolve))
        {
            engine = SharedRendererCascade.BuildEngine(
                canonicalDocument,
                styleSet,
                (int)viewport.Width,
                (int)viewport.Height);
            _authorEngine = SharedRendererCascade.BuildAuthorEngine(
                canonicalDocument,
                styleSet,
                (int)viewport.Width,
                (int)viewport.Height);

            // Item #12: resolve every element's cascade on the thread budget first, so the ordered
            // box walk below reads the engine's memo instead of computing. See CssStyleRecalc for
            // why the walk itself is not the thing that gets threaded.
            Broiler.Layout.Engine.CssStyleRecalc.Warm(root, engine);
        }

        using (Broiler.Layout.Diagnostics.RenderStageTrace.Measure(Broiler.Layout.Diagnostics.RenderStageTrace.SubStages.CascadeProject))
        {
            var combinedStyleSheet = styleSet.StyleSheet;
            CascadeApplyStyles(
                root,
                styleSet,
                baseUrl,
                engine,
                RendererStyleQueries.HasGeneratedPseudoElementRules(combinedStyleSheet, before: true),
                RendererStyleQueries.HasGeneratedPseudoElementRules(combinedStyleSheet, before: false));
            GenerateNativeBackdrops(root, engine, baseUrl);
            SetTextSelectionStyle(htmlContainer, root, engine);
        }

        using (Broiler.Layout.Diagnostics.RenderStageTrace.Measure(Broiler.Layout.Diagnostics.RenderStageTrace.SubStages.BoxFixups))
        {
            // CSS Display 3 §2.7 'blockification' and CSS Flexbox §3: a flex/grid container's
            // in-flow children are items, an item's display is blockified, and `float` has no
            // effect on one. Without it an inline-level item never reaches the block layout path
            // at all and lays out as nothing — which is every <a> and <span> in a flex toolbar —
            // and a floated one is taken out of flow, so the container sizes as if it were not
            // there. Runs ahead of the box fix-ups below so each of them sees the display the
            // item ends up with: an <img> item blockified to `block` is wrapped by
            // CorrectImgBoxes, and a blockified item takes part in the inline/block corrections
            // as the block-level box it has become.
            Broiler.Layout.Engine.FlexGridItemBlockification.Generate(root);

            CorrectTextBoxes(root);
            CorrectImgBoxes(root, baseUrl);
            CorrectObjectBoxes(root);
            CorrectFramesetBoxes(root);
            CorrectIframeBoxes(root);
            CorrectVideoBoxes(root);
            CorrectCanvasBoxes(root);
            CorrectProgressBoxes(root, baseUrl);
            CorrectSelectMultipleBoxes(root, baseUrl);

            // CSS2.1 §17.2.1 'generate missing parents': a table-row / row group /
            // table-cell sitting outside the table box it requires is neither block nor
            // inline, so block layout walked past it and it was dropped. Wrap it in the
            // anonymous table (and row) the spec calls for before the inline/block
            // corrections below, so the generated table takes part in them as the
            // block-level box it is.
            Broiler.Layout.Engine.AnonymousTableBoxes.Generate(root, baseUrl);

            bool followingBlock = true;
            CorrectLineBreaksBlocks(root, ref followingBlock);
            CorrectInlineBoxesParent(root, baseUrl);
            CorrectBlockInsideInline(root, baseUrl);
            CorrectInlineBoxesParent(root, baseUrl);
        }

        return root;
    }

    private void CascadeParseStyles(CssBox box, ref HtmlStyleSet styleSet)
    {
        // HTML §4.12.3: a <template>'s children are its *template contents*, held in a separate
        // document fragment. They are inert — not rendered, and their <style>/<link> do not style
        // the host document; a template is a stamp, and nothing on it applies until it is stamped
        // out. The contents already produce no boxes, but this walk collected their stylesheets
        // anyway, so a shadow-DOM component that keeps its styles in a <template> (the ordinary way
        // to write one) leaked them into the page. That is WPT issue #1491 problem 29:
        // delegatesFocus-highlight-sibling puts `:host { background-color: #aaa }` and
        // `:host(:focus) { background-color: #ccc }` in a template, and Broiler painted 99% of the
        // canvas #ccc against a reference that is 98% white.
        if (box.HtmlTag != null &&
            box.HtmlTag.Name.Equals("template", StringComparison.CurrentCultureIgnoreCase))
            return;

        if (box.HtmlTag != null)
        {
            // CSSOM §2.3 / HTML §4.2.6: a disabled stylesheet does not apply.
            // `HTMLLinkElement.disabled` / `HTMLStyleElement.disabled` (set from
            // script) are reflected onto the element as a `disabled` attribute, so
            // skip collecting rules from a <link>/<style> that carries it.
            bool sheetDisabled = box.GetAttribute("disabled", null) != null;

            // Check for the <link rel=stylesheet> tag
            // Per CSS2.1 §6.4.1, the rel attribute is a space-separated list;
            // match if any token equals "stylesheet" (e.g. rel="appendix stylesheet").
            if (!sheetDisabled &&
                box.HtmlTag.Name.Equals("link", StringComparison.CurrentCultureIgnoreCase) &&
                ContainsStylesheetRel(box.GetAttribute("rel", string.Empty)))
            {
                _stylesheetLoader.LoadStylesheet(box.GetAttribute("href", string.Empty), (Dictionary<string, string>)box.HtmlTag.Attributes, out string stylesheet, out Broiler.CSS.CssStyleSheet stylesheetModel);
                if (stylesheet != null)
                    styleSet = styleSet.AppendAuthorStyleSheet(new Broiler.CSS.CssParser().ParseStyleSheet(stylesheet));
                else if (stylesheetModel != null)
                    styleSet = styleSet.AppendAuthorStyleSheet(stylesheetModel);
            }

            // Check for the <style> tag
            if (!sheetDisabled &&
                box.HtmlTag.Name.Equals("style", StringComparison.CurrentCultureIgnoreCase) && box.Boxes.Count > 0)
            {
                foreach (var child in box.Boxes)
                    styleSet = styleSet.AppendAuthorStyleSheet(
                        new Broiler.CSS.CssParser().ParseStyleSheet(StripCdataSection(child.Text.ToString())));
            }
        }

        foreach (var childBox in box.Boxes)
            CascadeParseStyles(childBox, ref styleSet);
    }


    private void CascadeApplyStyles(
        CssBox box,
        HtmlStyleSet styleSet,
        Uri baseUrl,
        CSS.Dom.CssStyleEngine engine,
        bool hasBeforeRules,
        bool hasAfterRules)
    {
        box.InheritStyle();

        if (box.HtmlTag != null)
        {
            // Presentation attributes are low-priority author hints. Project the shared
            // origin-aware stylesheet + inline cascade over them. Every element box carries
            // a SourceElement, so this is the only author/UA cascade path.
            TranslateAttributes(box.HtmlTag, box);

            if (engine != null && box.SourceElement != null)
            {
                _presentationalHints.TryGetValue(box, out var hintKeys);
                SharedRendererCascade.ProjectCascadedStyle(box, engine, _authorEngine, hintKeys);
            }

            // Phase 2: Populate BoxKind and DOM-attribute properties on the box
            // so layout code can use these instead of accessing HtmlTag directly.
            AssignBoxKindAndAttributes(box);

            // HTML5 §4.8.9: <video> and <audio> are replaced elements. Browsers
            // that support these media types never display the fallback content
            // between the tags; they render the poster frame or first frame
            // instead.  Since this renderer cannot decode media streams, render
            // them as inline-block boxes with the default intrinsic dimensions
            // (300×150 for video, 300×54 for audio with controls).
            bool isVideo = box.HtmlTag.Name.Equals("video", StringComparison.OrdinalIgnoreCase);
            bool isAudio = !isVideo && box.HtmlTag.Name.Equals("audio", StringComparison.OrdinalIgnoreCase);
            bool hasControls = (isVideo || isAudio) && box.HtmlTag.HasAttribute("controls");

            // HTML rendering §15.4.7 UA stylesheet: `audio:not([controls])` is `display: none`.
            // Broiler laid it out as a box and filled it black, so a page listing many <audio>
            // elements — conformance-checkers/html/elements/audio/src-isvalid is 250 of them —
            // came out as a wall of black bars where the reference browser draws nothing at all.
            if (isAudio && !hasControls)
            {
                box.Display = CssConstants.None;
            }
            else if (isVideo || isAudio)
            {
                box.Display = CssConstants.InlineBlock;

                // Honour explicit width/height HTML attributes; fall back to the
                // default intrinsic size per the HTML spec.
                if (string.IsNullOrEmpty(box.Width) || box.Width == CssConstants.Auto)
                {
                    var attrW = box.HtmlTag.TryGetAttribute("width");
                    box.Width = !string.IsNullOrEmpty(attrW) ? attrW + "px" : "300px";
                }
                if (string.IsNullOrEmpty(box.Height) || box.Height == CssConstants.Auto)
                {
                    var attrH = box.HtmlTag.TryGetAttribute("height");
                    box.Height = !string.IsNullOrEmpty(attrH) ? attrH + "px" : (isVideo ? "150px" : "54px");
                }

                // Only a media element showing controls paints anything without decodable media:
                // HTML §4.8.9 says a <video> with no poster and no frame "represents nothing", and
                // the reference browser paints its box transparent — the control bar is the sole
                // thing on screen. The placeholder fill therefore follows `controls`, not the
                // element: a dark scrim under a video's controls, the light bar under an audio's.
                if (hasControls
                    && (string.IsNullOrEmpty(box.BackgroundColor)
                        || box.BackgroundColor.Equals("transparent", StringComparison.OrdinalIgnoreCase)))
                {
                    box.BackgroundColor = isVideo ? "black" : "#f1f3f4";
                }

                // Hide all children (fallback content, <source>, <track>, etc.)
                foreach (var child in box.Boxes)
                    child.Display = CssConstants.None;
            }

            // SVG §7.1: Inline <svg> elements are replaced elements rendered as
            // inline-block boxes.  Their child elements (rect, circle, path, etc.)
            // are not CSS-visible — the SVG subtree is serialised to markup and
            // rendered later by SvgRenderer via PaintWalker.
            if (!isVideo && !isAudio &&
                box.HtmlTag.Name.Equals("svg", StringComparison.OrdinalIgnoreCase))
            {
                // CSS Display 3 §2 / SVG 2 §7.1: `inline` is an *outermost* <svg>'s initial
                // display, not a fixed one, and `inline-block` is how an inline replaced box is
                // laid out here — so that substitution belongs to the initial value alone.
                // Applying it to every value discarded the author's `display` outright:
                // `svg { display: block }` — the ordinary way to take an SVG off the text
                // baseline — laid the element out inline, so siblings sat side by side instead
                // of stacking and `margin: 10px auto` computed to zero rather than centring it
                // (css/compositing/line-with-svg-background is ten such block <svg>s and came
                // out two to a row), and `svg { display: none }` was overridden into a visible
                // box, so an author-hidden SVG painted.
                //
                // A *nested* <svg> is SVG content rather than a CSS box, and the loop below
                // deliberately hides every child of the SVG it sits in. Reading the author's
                // display off such a box would read that hiding back as `display: none` and
                // drop the inner viewport for good, so only the outermost <svg> — the one that
                // really is a replaced element in the host document — takes the cascaded value.
                if (!HasSvgAncestor(box))
                {
                    if (box.Display == CssConstants.Inline)
                        box.Display = CssConstants.InlineBlock;
                }
                else
                {
                    box.Display = CssConstants.InlineBlock;
                }

                ApplySvgReplacedSizing(box);

                // Overflow hidden to clip SVG content at the element bounds.
                if (string.IsNullOrEmpty(box.Overflow) || box.Overflow == CssConstants.Visible)
                    box.Overflow = CssConstants.Hidden;

                // Hide child boxes so the CSS layout engine ignores SVG internals.
                foreach (var child in box.Boxes)
                    child.Display = CssConstants.None;
            }
        }

        // CSS2.1 §9.7: Relationships between 'display', 'position', and 'float'.
        // When 'float' is not 'none', the computed value of 'display' is adjusted
        // so that inline-level elements become block-level.  This must happen
        // after all CSS properties are resolved (including 'inherit') and before
        // child style cascading so children see the correct parent display value.
        if (box.Display != CssConstants.None && box.Float != CssConstants.None)
        {
            if (box.Display == CssConstants.Inline || box.Display == CssConstants.InlineBlock)
                box.Display = CssConstants.Block;
        }

        if (box.TextDecoration != string.Empty && box.Text.IsEmpty)
        {
            foreach (var childBox in box.Boxes)
                childBox.TextDecoration = box.TextDecoration;

            box.TextDecoration = string.Empty;
        }

        // CSS Animations §3: Resolve animation keyframe values for static
        // rendering.  After all CSS rules and inline styles are applied,
        // check if the box has an animation-name that references a known
        // @keyframes rule and apply the computed animated values.
        CssAnimationResolver.ResolveAnimations(box, styleSet.AuthorStyleSheet);

        foreach (var childBox in box.Boxes)
            CascadeApplyStyles(childBox, styleSet, baseUrl, engine, hasBeforeRules, hasAfterRules);

        if (box.HtmlTag != null)
            ApplyClosedDetailsVisibility(box);

        if (box.HtmlTag != null)
            ApplySummaryDisclosureMarker(box, baseUrl);

        // CSS2.1 §12.1: Generate ::before and ::after pseudo-element boxes
        // after child style cascading to avoid modifying the child list
        // during iteration.
        if (box.HtmlTag != null && (hasBeforeRules || hasAfterRules))
            ApplyPseudoElementBoxes(box, engine, baseUrl, hasBeforeRules, hasAfterRules);
    }

    private static void ApplyClosedDetailsVisibility(CssBox box)
    {
        // HTML §4.11.1: Closed <details> elements expose their first
        // <summary> but keep the rest of the subtree hidden until the open
        // attribute is present.
        if (!box.HtmlTag.Name.Equals("details", StringComparison.OrdinalIgnoreCase) ||
            box.HtmlTag.HasAttribute("open"))
        {
            return;
        }

        bool seenSummary = false;
        foreach (var child in box.Boxes)
        {
            if (child.HtmlTag != null &&
                child.HtmlTag.Name.Equals("summary", StringComparison.OrdinalIgnoreCase) &&
                !seenSummary)
            {
                seenSummary = true;
                continue;
            }

            child.Display = CssConstants.None;
        }
    }

    private static void ApplySummaryDisclosureMarker(CssBox box, Uri baseUrl)
    {
        if (!box.HtmlTag.Name.Equals("summary", StringComparison.OrdinalIgnoreCase) ||
            box.ParentBox?.HtmlTag == null ||
            !box.ParentBox.HtmlTag.Name.Equals("details", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (box.Boxes.Count > 0 &&
            box.Boxes[0].HtmlTag == null &&
            box.Boxes[0].Text.Length > 0 &&
            (box.Boxes[0].Text.Span.SequenceEqual("▸ ".AsSpan()) ||
             box.Boxes[0].Text.Span.SequenceEqual("▾ ".AsSpan())))
        {
            return;
        }

        var markerText = box.ParentBox.HtmlTag.HasAttribute("open") ? "▾ " : "▸ ";
        var markerBox = box.Boxes.Count > 0
            ? CssBoxHelper.CreateBox(box, baseUrl, before: box.Boxes[0])
            : CssBoxHelper.CreateBox(box, baseUrl);
        markerBox.Display = CssConstants.Inline;
        markerBox.Text = markerText.AsMemory();
    }

    private static void SetTextSelectionStyle(
        HtmlContainerInt htmlContainer,
        CssBox root,
        Broiler.CSS.Dom.CssStyleEngine engine)
    {
        htmlContainer.SelectionForeColor = BColor.Empty;
        htmlContainer.SelectionBackColor = BColor.Empty;

        if (engine == null || SharedRendererCascade.FindCanonicalDocument(root)?.DocumentElement is not { } element)
            return;

        var style = engine.GetCascadedStyle(element, "::selection");
        if (style.TryGetValue("color", out var foreground))
            htmlContainer.SelectionForeColor = htmlContainer.ParseCssColor(foreground);

        if (style.TryGetValue("background-color", out var background))
            htmlContainer.SelectionBackColor = htmlContainer.ParseCssColor(background);
    }

    /// <summary>
    /// Creates generated-content boxes from the shared pseudo-element cascade.
    /// </summary>
    private static void ApplyPseudoElementBoxes(
        CssBox box,
        Broiler.CSS.Dom.CssStyleEngine engine,
        Uri baseUrl,
        bool hasBeforeRules,
        bool hasAfterRules)
    {
        if (engine == null || box.SourceElement == null)
            return;

        if (hasBeforeRules)
        {
            var before = engine.GetCascadedStyle(box.SourceElement, "::before");
            if (before.ContainsKey("content"))
                CreatePseudoElementBox(box, before, isBefore: true, baseUrl);
        }

        if (hasAfterRules)
        {
            var after = engine.GetCascadedStyle(box.SourceElement, "::after");
            if (after.ContainsKey("content"))
                CreatePseudoElementBox(box, after, isBefore: false, baseUrl);
        }
    }

    /// <summary>
    /// Creates a pseudo-element <see cref="CssBox"/> as a child of
    /// <paramref name="parentBox"/> with styles from <paramref name="properties"/>.
    /// For <c>::before</c>, the box is inserted as the first child;
    /// for <c>::after</c>, it is appended as the last child.
    /// </summary>
    private static void CreatePseudoElementBox(
        CssBox parentBox,
        IReadOnlyDictionary<string, string> properties,
        bool isBefore,
        Uri baseUrl)
    {
        // Determine content value — skip generation for "none" and "normal".
        string contentValue = null;
        if (properties.TryGetValue("content", out string cv))
            contentValue = cv;

        if (contentValue == null || contentValue == "none" || contentValue == "normal")
            return;

        // Create the pseudo-element box and inherit from parent.
        CssBox pseudoBox;
        if (isBefore && parentBox.Boxes.Count > 0)
        {
            var firstChild = parentBox.Boxes[0];
            pseudoBox = CssBoxHelper.CreateBox(parentBox, before: firstChild, baseUrl: baseUrl);
        }
        else
        {
            pseudoBox = CssBoxHelper.CreateBox(parentBox, baseUrl);
        }

        // Apply pseudo-element CSS declarations.
        foreach (var prop in properties)
        {
            var value = prop.Value;
            if (value == CssConstants.Inherit)
                value = CssUtils.GetPropertyValue(parentBox, prop.Key);
            CssUtils.SetPropertyValue(pseudoBox, prop.Key, value);
        }

        if (TryExtractPseudoElementImageUrl(contentValue, out var imageUrl))
        {
            // The image is rendered by the nested CssBoxImage below. Reset the
            // wrapper box's content value so the extracted URL is not retained as
            // generic generated content on the wrapper, which would otherwise
            // make later pseudo-box handling treat the wrapper as still owning
            // the original url(...) payload instead of the nested image box.
            pseudoBox.Content = CssConstants.Normal;

            var imageTag = new HtmlTag(
                HtmlConstants.Img,
                true,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["src"] = imageUrl
                });
            _ = new CssBoxImage(pseudoBox, imageTag, baseUrl);
            return;
        }

        // Set text content (strip surrounding quotes from CSS content value).
        var text = contentValue.Trim('\'', '"');
        if (text.Length > 0)
            pseudoBox.Text = text.AsMemory();
    }

    // Bridge markers (mirrored from DomBridge.AnchorResolver.Dialogs): the resolved backdrop
    // background the bridge stamps on a top-layer element in NativeTopLayer mode, and the
    // top-layer order it stamps alongside. Absent on the baked path, so backdrop generation is a
    // no-op there.
    private const string BackdropBgAttr = "data-broiler-backdrop";
    private const string TopLayerOrderMarkerAttr = "data-broiler-top-layer";

    // Author ::backdrop declarations that override the viewport-covering geometry defaults (an
    // explicitly sized/positioned backdrop). Background is not overlaid — the bridge already
    // folded any author background into the resolved value on the marker.
    private static readonly string[] BackdropGeometryProps =
        { "width", "height", "top", "left", "right", "bottom", "position" };

    // Author ::backdrop declarations that change how the scrim composites rather than where it
    // sits. Without these the box painted with the resolved background and nothing else, so an
    // author `opacity: 0.5` on a green scrim filled the viewport with opaque green instead of
    // compositing over the canvas (WPT the-dialog-element/modal-dialog-backdrop-opacity). Kept
    // in step with the bridge's BackdropPaintingProps, which does the same for the synthesized
    // <div> the NativeBackdrop-off path builds.
    private static readonly string[] BackdropPaintingProps =
        { "opacity", "mix-blend-mode", "border-radius", "box-shadow" };

    /// <summary>
    /// CSS Position 4 §top-layer / HTML §dialog: generates a native <c>::backdrop</c> box for each
    /// element the bridge marked with a resolved backdrop background
    /// (<c>data-broiler-backdrop</c>) — an open modal dialog or open popover in
    /// <c>NativeTopLayer</c> mode. The <c>::backdrop</c> is a top-layer box (order from the
    /// element's <c>data-broiler-top-layer</c> marker) inserted as a sibling <em>before</em> the
    /// element, so <c>PaintWalker.PaintTopLayer</c> paints it directly beneath the element yet
    /// above ordinary page content — replacing the bridge's synthesized backdrop <c>&lt;div&gt;</c>
    /// (which mutated the box tree). A no-op on the baked path, where the marker is absent.
    /// </summary>
    private static void GenerateNativeBackdrops(CssBox root, Broiler.CSS.Dom.CssStyleEngine engine, Uri baseUrl)
    {
        // Collect first — inserting siblings mutates the parents' child lists.
        var targets = new List<CssBox>();
        CollectBackdropTargets(root, targets);
        foreach (var dialogBox in targets)
            CreateNativeBackdropBox(dialogBox, engine, baseUrl);
    }

    private static void CollectBackdropTargets(CssBox box, List<CssBox> targets)
    {
        if (box.HtmlTag != null && box.HtmlTag.HasAttribute(BackdropBgAttr))
            targets.Add(box);
        foreach (var child in box.Boxes)
            CollectBackdropTargets(child, targets);
    }

    private static void CreateNativeBackdropBox(CssBox dialogBox, Broiler.CSS.Dom.CssStyleEngine engine, Uri baseUrl)
    {
        var parent = dialogBox.ParentBox;
        if (parent == null)
            return;

        var bg = dialogBox.HtmlTag.TryGetAttribute(BackdropBgAttr, "transparent");
        int order = 0;
        var orderRaw = dialogBox.HtmlTag.TryGetAttribute(TopLayerOrderMarkerAttr);
        if (!string.IsNullOrEmpty(orderRaw))
            int.TryParse(orderRaw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out order);

        // Insert the ::backdrop as a sibling immediately before the element, so the top-layer
        // paint's document-order tiebreak paints it beneath the element.
        var backdrop = CssBoxHelper.CreateBox(parent, baseUrl, before: dialogBox);

        // UA ::backdrop box: fixed, covering the viewport (inset:0, resolved natively), with the
        // bridge-resolved background (UA modal/popover scrim default folded with author background).
        CssUtils.SetPropertyValue(backdrop, "position", "fixed");
        CssUtils.SetPropertyValue(backdrop, "top", "0");
        CssUtils.SetPropertyValue(backdrop, "left", "0");
        CssUtils.SetPropertyValue(backdrop, "right", "0");
        CssUtils.SetPropertyValue(backdrop, "bottom", "0");
        CssUtils.SetPropertyValue(backdrop, "background-color", bg);

        // Overlay author ::backdrop geometry (an explicitly sized/positioned backdrop) and the
        // painting properties that composite it; the background stays the bridge-resolved value.
        if (engine != null && dialogBox.SourceElement != null)
        {
            var decls = engine.GetCascadedStyle(dialogBox.SourceElement, "::backdrop");
            foreach (var prop in BackdropGeometryProps)
                if (decls.TryGetValue(prop, out var val) && !string.IsNullOrWhiteSpace(val))
                    CssUtils.SetPropertyValue(backdrop, prop, val.Trim());
            foreach (var prop in BackdropPaintingProps)
                if (decls.TryGetValue(prop, out var val) && !string.IsNullOrWhiteSpace(val))
                    CssUtils.SetPropertyValue(backdrop, prop, val.Trim());
        }

        backdrop.TopLayerOrder = order;
    }

    private static bool TryExtractPseudoElementImageUrl(string contentValue, out string imageUrl)
    {
        imageUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(contentValue))
            return false;

        var trimmed = contentValue.Trim();
        if (trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(")"))
        {
            imageUrl = trimmed[4..^1].Trim();
        }
        else if (trimmed.StartsWith("/", StringComparison.Ordinal)
            || trimmed.StartsWith("./", StringComparison.Ordinal)
            || trimmed.StartsWith("../", StringComparison.Ordinal)
            || trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            imageUrl = trimmed;
        }
        else
        {
            return false;
        }
        if (imageUrl.Length >= 2 &&
            ((imageUrl[0] == '\'' && imageUrl[^1] == '\'') ||
             (imageUrl[0] == '"' && imageUrl[^1] == '"')))
        {
            imageUrl = imageUrl[1..^1];
        }

        return imageUrl.Length > 0;
    }

    /// <summary>
    /// Returns <c>true</c> when the space-separated <c>rel</c> attribute value
    /// contains the token <c>stylesheet</c> (case-insensitive).
    /// This allows <c>&lt;link rel="appendix stylesheet"&gt;</c> to be recognised
    /// as a stylesheet link, as required by CSS2.1 §6.4.1 and the Acid2 test.
    /// </summary>
    private static bool ContainsStylesheetRel(string relValue)
    {
        if (string.IsNullOrEmpty(relValue))
            return false;

        foreach (var token in relValue.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("stylesheet", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Phase 2: Sets <see cref="CssBoxProperties.Kind"/>, list attributes
    /// (<see cref="CssBoxProperties.ListStart"/>, <see cref="CssBoxProperties.ListReversed"/>),
    /// and <see cref="CssBoxProperties.ImageSource"/> based on the HTML tag.
    /// This allows layout code to consume these properties instead of reading
    /// <see cref="HtmlTag"/> attributes directly.
    /// </summary>
    private static void AssignBoxKindAndAttributes(CssBox box)
    {
        var tag = box.HtmlTag;
        if (tag == null)
            return;

        box.Kind = tag.Name.ToLowerInvariant() switch
        {
            HtmlConstants.Img => BoxKind.ReplacedImage,
            HtmlConstants.Iframe => BoxKind.ReplacedIframe,
            HtmlConstants.Table => BoxKind.Table,
            HtmlConstants.Tr => BoxKind.TableRow,
            HtmlConstants.Td or HtmlConstants.Th => BoxKind.TableCell,
            HtmlConstants.Li => BoxKind.ListItem,
            HtmlConstants.Ol => BoxKind.OrderedList,
            HtmlConstants.Ul => BoxKind.UnorderedList,
            HtmlConstants.Hr => BoxKind.HorizontalRule,
            HtmlConstants.Br => BoxKind.LineBreak,
            HtmlConstants.A => BoxKind.Anchor,
            HtmlConstants.Font => BoxKind.Font,
            HtmlConstants.Input => BoxKind.Input,
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => BoxKind.Heading,
            "object" when box is CssBoxImage => BoxKind.ReplacedImage,
            _ => BoxKind.Anonymous,
        };

        // Populate list attributes for <ol> elements
        if (box.Kind == BoxKind.OrderedList)
        {
            box.ListReversed = tag.HasAttribute("reversed");
            if (int.TryParse(tag.TryGetAttribute("start"), out int start))
                box.ListStart = start;
        }

        // Populate image source for <img> and <object> image elements
        if (box.Kind == BoxKind.ReplacedImage)
            box.ImageSource = tag.TryGetAttribute("src") ?? tag.TryGetAttribute("data");
    }

    private void TranslateAttributes(HtmlTag tag, CssBox box)
    {
        if (!tag.HasAttributes())
            return;

        // HTML §4.12.5: a <canvas>'s width/height content attributes are the dimensions of its
        // *bitmap*, and the Rendering section maps no presentation width/height for it — unlike
        // <img>, <table> and the rest below. Projecting them onto CSS width/height made the two axes
        // independently stated, so `max-width`/`max-height` clamped each on its own instead of
        // keeping the natural ratio; CorrectCanvasBoxes records them as the natural size instead.
        bool isCanvas = tag.Name.Equals("canvas", StringComparison.OrdinalIgnoreCase);

        foreach (string att in tag.Attributes.Keys)
        {
            string value = tag.Attributes[att];

            if (isCanvas && (att == HtmlConstants.Width || att == HtmlConstants.Height))
                continue;

            switch (att)
            {
                case HtmlConstants.Align:
                    if (value == HtmlConstants.Left || value == HtmlConstants.Center || value == HtmlConstants.Right || value == HtmlConstants.Justify)
                        box.TextAlign = value.ToLower();
                    else
                        box.VerticalAlign = value.ToLower();
                    break;
                case HtmlConstants.Background:
                    box.BackgroundImage = value.ToLower();
                    break;
                case HtmlConstants.Bgcolor:
                    box.BackgroundColor = value.ToLower();
                    break;
                case HtmlConstants.Border:
                    if (!string.IsNullOrEmpty(value) && value != "0")
                    {
                        box.BorderLeftStyle = box.BorderTopStyle = box.BorderRightStyle = box.BorderBottomStyle = CssConstants.Solid;
                        // Legacy `<table border>` paints grey borders by default;
                        // previously supplied by a UA `table { border-color }`
                        // rule, removed because it blocked author shorthands
                        // (CssDefaults.cs). Apply the grey directly on the
                        // attribute path so author CSS is unaffected.
                        box.BorderLeftColor = box.BorderTopColor = box.BorderRightColor = box.BorderBottomColor = "#dfdfdf";
                    }
                    box.BorderLeftWidth = box.BorderTopWidth = box.BorderRightWidth = box.BorderBottomWidth = TranslateLength(value);

                    if (tag.Name == HtmlConstants.Table)
                    {
                        if (value != "0")
                            ApplyTableBorder(box, "1px");
                    }
                    else
                    {
                        box.BorderTopStyle = box.BorderLeftStyle = box.BorderRightStyle = box.BorderBottomStyle = CssConstants.Solid;
                    }
                    break;
                case HtmlConstants.Bordercolor:
                    box.BorderLeftColor = box.BorderTopColor = box.BorderRightColor = box.BorderBottomColor = value.ToLower();
                    break;
                case HtmlConstants.Cellspacing:
                    box.BorderSpacing = TranslateLength(value);
                    RecordPresentationalHint(box, "border-spacing");
                    break;
                case HtmlConstants.Cellpadding:
                    ApplyTablePadding(box, value);
                    break;
                case HtmlConstants.Color:
                    box.Color = value.ToLower();
                    break;
                case HtmlConstants.Dir:
                    box.Direction = value.ToLower();
                    break;
                case HtmlConstants.Face:
                    box.FontFamily = RendererStyleQueries.UnescapeIdentifier(
                        value.Split(',')[0].Trim().Trim('"', '\''));
                    break;
                case HtmlConstants.Height:
                    box.Height = TranslateLength(value);
                    break;
                case HtmlConstants.Hspace:
                    box.MarginRight = box.MarginLeft = TranslateLength(value);
                    break;
                case HtmlConstants.Nowrap:
                    box.WhiteSpace = CssConstants.NoWrap;
                    break;
                case HtmlConstants.Size:
                    if (tag.Name.Equals(HtmlConstants.Hr, StringComparison.OrdinalIgnoreCase))
                        box.Height = TranslateLength(value);
                    else if (tag.Name.Equals(HtmlConstants.Font, StringComparison.OrdinalIgnoreCase))
                        box.FontSize = value;
                    else if (tag.Name.Equals(HtmlConstants.Input, StringComparison.OrdinalIgnoreCase))
                    {
                        // HTML5 §4.10.5.3.7: The size attribute on <input>
                        // specifies the visible width in average-character
                        // units.  Approximate using ~8px per character
                        // (roughly 1ex at 13.3333px Arial, matching
                        // Chromium's default rendering of size=20 → ~173px).
                        const double AvgCharWidthPx = 8.05;
                        const double InputPaddingBorderPx = 12; // padding + border on both sides
                        if (int.TryParse(value, out int chars) && chars > 0)
                            box.Width = $"{chars * AvgCharWidthPx + InputPaddingBorderPx}px";
                    }
                    break;
                case HtmlConstants.Valign:
                    box.VerticalAlign = value.ToLower();
                    break;
                case HtmlConstants.Vspace:
                    box.MarginTop = box.MarginBottom = TranslateLength(value);
                    break;
                case HtmlConstants.Width:
                    box.Width = TranslateLength(value);
                    break;
            }
        }
    }

    private static string TranslateLength(string htmlLength)
    {
        // `auto` is a keyword, not a length: appending "px" turned it into the
        // unparseable "autopx", which every consumer resolved to zero — an
        // `<svg width="auto">` collapsed to a zero-width box and painted nothing
        // instead of falling back to the element's auto sizing.
        if (htmlLength != null && htmlLength.Trim().Equals(CssConstants.Auto, StringComparison.OrdinalIgnoreCase))
            return CssConstants.Auto;

        return CssLengthParser.IsValidLength(htmlLength)
            ? htmlLength
            : $"{htmlLength}px";
    }

    // XHTML wraps inline <style> CSS in a CDATA section
    // (<![CDATA[ ... ]]>) so the markup validates as XML. When such a document
    // is parsed by the HTML tree builder the markers stay as literal text inside
    // the style element, and the CSS parser cannot tokenize "<![CDATA[" / "]]>"
    // — it drops the rules, so the whole stylesheet is silently lost (every
    // CDATA-wrapped CSS2 .xht reftest, WPT issue #1143). The markers are never
    // valid CSS, so strip them before parsing.
    private static string StripCdataSection(string css)
    {
        if (string.IsNullOrEmpty(css) || css.IndexOf("CDATA", StringComparison.Ordinal) < 0)
            return css;
        return css.Replace("<![CDATA[", string.Empty, StringComparison.Ordinal)
                  .Replace("]]>", string.Empty, StringComparison.Ordinal);
    }

    private static void ApplyTableBorder(CssBox table, string border) => SetForAllCells(table, cell =>
    {
        cell.BorderLeftStyle = cell.BorderTopStyle = cell.BorderRightStyle = cell.BorderBottomStyle = CssConstants.Solid;
        cell.BorderLeftWidth = cell.BorderTopWidth = cell.BorderRightWidth = cell.BorderBottomWidth = border;
        // Legacy `<table border>` cells render with the UA grey border color.
        // Previously this came from a blanket `td, th { border-color:#dfdfdf }`
        // UA rule, which was removed because it blocked author `border`
        // shorthands (CssDefaults.cs); set the grey here so the attribute path
        // keeps its default while author CSS on cells is unaffected.
        cell.BorderLeftColor = cell.BorderTopColor = cell.BorderRightColor = cell.BorderBottomColor = "#dfdfdf";
    });

    private void ApplyTablePadding(CssBox table, string padding)
    {
        var length = TranslateLength(padding);
        SetForAllCells(table, cell =>
        {
            cell.PaddingLeft = cell.PaddingTop = cell.PaddingRight = cell.PaddingBottom = length;
            RecordPresentationalHint(cell, "padding-left", "padding-top", "padding-right", "padding-bottom");
        });
    }

    private static void SetForAllCells(CssBox table, ActionInt<CssBox> action)
    {
        foreach (var l1 in table.Boxes)
        {
            foreach (var l2 in l1.Boxes)
            {
                if (l2.HtmlTag != null && l2.HtmlTag.Name == "td")
                {
                    action(l2);
                }
                else
                {
                    foreach (var l3 in l2.Boxes)
                    {
                        action(l3);
                    }
                }
            }
        }
    }

    private static void CorrectTextBoxes(CssBox box)
    {
        for (int i = box.Boxes.Count - 1; i >= 0; i--)
        {
            var childBox = box.Boxes[i];
            if (!childBox.Text.IsEmpty)
            {
                // is the box has text
                var keepBox = !childBox.Text.Span.IsWhiteSpace();

                // is the box is pre-formatted
                keepBox = keepBox || childBox.WhiteSpace == CssConstants.Pre || childBox.WhiteSpace == CssConstants.PreWrap;

                // is the box is only one in the parent
                keepBox = keepBox || box.Boxes.Count == 1;

                // is it a whitespace between two inline boxes
                keepBox = keepBox || (i > 0 && i < box.Boxes.Count - 1 && box.Boxes[i - 1].IsInline && box.Boxes[i + 1].IsInline);

                // is first/last box where is in inline box and it's next/previous box is inline
                keepBox = keepBox || (i == 0 && box.Boxes.Count > 1 && box.Boxes[1].IsInline && box.IsInline) || (i == box.Boxes.Count - 1 && box.Boxes.Count > 1 && box.Boxes[i - 1].IsInline && box.IsInline);

                if (keepBox)
                {
                    // valid text box, parse it to words
                    childBox.ParseToWords();
                }
                else
                {
                    // remove text box that has no 
                    childBox.ParentBox.Boxes.RemoveAt(i);
                }
            }
            else
            {
                // recursive
                CorrectTextBoxes(childBox);
            }
        }
    }

    private static void CorrectImgBoxes(CssBox box, Uri baseUrl)
    {
        for (int i = box.Boxes.Count - 1; i >= 0; i--)
        {
            var childBox = box.Boxes[i];

            // A row flex container's item is never wrapped: the wrapper would become the item, so
            // every size the flex algorithm resolves would land on it and the image inside would
            // keep the width it was declared with. See FlexGridItemBlockification.IsRowFlexItem,
            // which is where that decision and the blockification that creates this `block`
            // display live together.
            if (childBox is CssBoxImage && childBox.Display == CssConstants.Block
                && !Broiler.Layout.Engine.FlexGridItemBlockification.IsRowFlexItem(childBox))
            {
                // Asked before the reparent, while the image is still the flex container's own
                // child: the predicate reads the *element's* width, margins and alignment, which
                // are what §9.4 step 11 turns on, and the wrapper about to be inserted has none of
                // them.
                bool fillsStretchedItem =
                    Broiler.Layout.Engine.FlexGridItemBlockification.IsStretchedColumnFlexItem(childBox);

                var block = CssBoxHelper.CreateBlock(childBox.ParentBox, baseUrl, null, childBox);
                childBox.ParentBox = block;
                childBox.Display = CssConstants.Inline;

                // The wrapper is now the block-level box the element generates, so what `page`
                // names travels with it. CSS Paged Media 3 §3.4 hangs a page name on a block-level
                // box and nothing else, and the image is about to stop being one — leaving the name
                // behind on the demoted inline means an `<img style="display:block; page:b">` is
                // read as staying on its ancestor's page. `css-page/page-name-img-003` and `-004`
                // are exactly that, against `-001` and `-002` where the image really is inline and
                // its name really must be ignored.
                block.Page = childBox.Page;

                // CSS2.1 §10.3.4: a block-level replaced element resolves its
                // horizontal margins with the non-replaced-block rules, so
                // `margin-left/right:auto` can center it or push it to one side.
                // The image is painted as an inline replaced word inside this
                // anonymous block wrapper, and auto side-margins on an *inline*
                // replaced box compute to 0 — so without help the image is stuck
                // flush-left (WPT c43-rpl-bbx-001: `width:50%; margin-left:auto`
                // must be flush right). When the replaced element has a definite
                // width and an auto side-margin, hand the block-level width and
                // horizontal margins to the WRAPPER (which runs the block
                // auto-margin resolution) and let the inline image fill it.
                bool hasAutoSideMargin = childBox.MarginLeft == CssConstants.Auto
                                      || childBox.MarginRight == CssConstants.Auto;
                bool hasDefiniteWidth = !string.IsNullOrEmpty(childBox.Width)
                                      && childBox.Width != CssConstants.Auto;
                if (hasAutoSideMargin && hasDefiniteWidth)
                {
                    block.Width = childBox.Width;
                    block.MarginLeft = childBox.MarginLeft;
                    block.MarginRight = childBox.MarginRight;
                    block.MaxWidth = childBox.MaxWidth;
                    block.MinWidth = childBox.MinWidth;
                    childBox.Width = "100%";
                    childBox.MarginLeft = "0";
                    childBox.MarginRight = "0";
                }

                // A column flex item's stretch lands on the wrapper, and to the spec the image
                // *is* the item — so it has to fill what was stretched, or it keeps an `auto`
                // width and falls back to the 300x150 default object size. The predicate lives
                // beside IsRowFlexItem so the two readings of "what is the item" cannot drift.
                else if (fillsStretchedItem)
                {
                    childBox.Width = "100%";
                }
            }
            else
            {
                // recursive
                CorrectImgBoxes(childBox, baseUrl);
            }
        }
    }

    /// <summary>
    /// Implements the <c>&lt;object&gt;</c> fallback chain (HTML4 §13.3):
    /// when an <c>&lt;object&gt;</c> element's <c>data</c> attribute points to a
    /// supported image (<c>data:image/…</c>), it is rendered as a replaced image
    /// and its children (fallback content) are removed.  Otherwise, children
    /// are kept as fallback content.
    /// </summary>
    /// <summary>
    /// Lays out <c>&lt;frameset&gt;</c> / <c>&lt;frame&gt;</c> as a nested-browsing-
    /// context grid (HTML §"the frameset element"): the frameset partitions its area
    /// per its <c>cols</c>/<c>rows</c> attributes and each frame (or nested frameset)
    /// fills one cell.  A cell's document is rasterised by the image renderer.  The
    /// outermost frameset fills the viewport; nested framesets fill their parent cell.
    /// <c>&lt;noframes&gt;</c> fallback content is hidden because frames are supported.
    /// </summary>
    /// <summary>
    /// HTML §4.8.5: an <c>&lt;iframe&gt;</c> is a replaced element hosting a nested browsing context.
    /// UAs that support iframes never render the inline fallback content between the tags — the loaded
    /// sub-document replaces it. This static renderer cannot load a sub-document, so the iframe paints as
    /// an empty replaced box (its UA <c>border: 2px inset</c> at the explicit/auto size) and its fallback
    /// children must not lay out or paint. Runs post-cascade so the per-box cascade cannot re-show a block
    /// child (e.g. a <c>&lt;div&gt;</c>) after the fact — the reason a cascade-time hide is insufficient.
    /// </summary>
    private static void CorrectIframeBoxes(CssBox box)
    {
        if (box.HtmlTag != null
            && box.HtmlTag.Name.Equals("iframe", StringComparison.OrdinalIgnoreCase))
        {
            // Author CSS sizing wins; otherwise the width/height presentation attributes, then the CSS
            // replaced-element default object size 300×150 — the same precedence CorrectVideoBoxes uses.
            // Without a default an unsized iframe collapsed to its border and neither the frame box nor
            // the projected sub-document rendered (WPT resource-timing/tentative/initiator-url/
            // static-resource). This must not be a UA-stylesheet width/height: that form outranks the
            // presentation attributes, so `iframe.width = 100` stopped taking effect
            // (the-dialog-element/centering, popovers/popover-move-documents).
            if (box.Width == CssConstants.Auto)
                box.Width = PresentationLengthPx(box, "width", "300px");
            if (box.Height == CssConstants.Auto)
                box.Height = PresentationLengthPx(box, "height", "150px");

            // display:none on each direct child hides that child and its whole subtree.
            foreach (var child in box.Boxes)
                child.Display = CssConstants.None;
            return;
        }

        foreach (var child in box.Boxes)
            CorrectIframeBoxes(child);
    }

    /// <summary>
    /// HTML §4.8.9: a <c>&lt;video&gt;</c> is a replaced element. Broiler cannot decode video streams, so —
    /// like a supporting UA with no poster/frame to show — it paints as an inline-block replaced box at its
    /// used size (the CSS-default intrinsic 300×150, or an author CSS size / the <c>width</c>/<c>height</c>
    /// presentation attributes), and its inline fallback content between the tags does not lay
    /// out or paint. Runs post-cascade so a cascade-time hide of a block fallback child cannot be re-shown
    /// (the same reason <see cref="CorrectIframeBoxes"/> is post-cascade). This is the native replacement for
    /// the bridge's <c>HtmlPostProcessor.ReplaceVideoWithPlaceholder</c> string rewrite.
    /// <para>The box is filled only when the element shows <c>controls</c>. The spec says a video with
    /// neither poster nor frame "represents nothing", and the reference browser paints its box
    /// transparent — a black fill made every source-less <c>&lt;video&gt;</c> on a page a solid black
    /// rectangle over whatever it sits on.</para>
    /// </summary>
    private static void CorrectVideoBoxes(CssBox box)
    {
        if (box.HtmlTag != null
            && box.HtmlTag.Name.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            box.Display = CssConstants.InlineBlock;
            // Author CSS sizing wins; otherwise the width/height presentation attributes, then the CSS
            // replaced-element intrinsic default 300×150.
            if (box.Width == CssConstants.Auto)
                box.Width = PresentationLengthPx(box, "width", "300px");
            if (box.Height == CssConstants.Auto)
                box.Height = PresentationLengthPx(box, "height", "150px");
            if (box.HtmlTag.HasAttribute("controls"))
                box.BackgroundColor = "black";
            foreach (var child in box.Boxes)
                child.Display = CssConstants.None;
            return;
        }

        foreach (var child in box.Boxes)
            CorrectVideoBoxes(child);
    }

    /// <summary>
    /// HTML §4.12.5: a <c>&lt;canvas&gt;</c> is a replaced element whose <b>natural</b> size is its
    /// <c>width</c>/<c>height</c> content attributes, defaulting to the 300×150 bitmap. Natural, not
    /// a CSS width and height: the distinction only shows once <c>max-width</c>/<c>max-height</c>
    /// clamp it, where the two axes stay tied by the natural ratio (CSS2.1 §10.4), which is what WPT
    /// <c>css-sizing/replaced-max-size-saturation</c> asserts. Broiler has no 2D context, so the
    /// bitmap is transparent and only the element's own background and border paint — and, like any
    /// UA that supports canvas, it never renders the fallback content between the tags. Runs
    /// post-cascade for the same reason <see cref="CorrectIframeBoxes"/> does: a cascade-time hide of
    /// a block fallback child can be re-shown afterwards.
    /// </summary>
    private static void CorrectCanvasBoxes(CssBox box)
    {
        if (box.HtmlTag != null
            && box.HtmlTag.Name.Equals("canvas", StringComparison.OrdinalIgnoreCase))
        {
            // Only the UA default display is replaced by the atomic inline-level box; an author
            // `display` (block, grid, none, …) is theirs to keep.
            if (box.Display == CssConstants.Inline)
                box.Display = CssConstants.InlineBlock;

            box.IntrinsicReplacedSize = new System.Drawing.SizeF(
                CanvasBitmapDimension(box, "width", 300),
                CanvasBitmapDimension(box, "height", 150));

            foreach (var child in box.Boxes)
                child.Display = CssConstants.None;
            return;
        }

        foreach (var child in box.Boxes)
            CorrectCanvasBoxes(child);
    }

    /// <summary>
    /// HTML §4.12.5: reads a <c>&lt;canvas&gt;</c>'s <c>width</c>/<c>height</c> content attribute as
    /// a valid non-negative integer, falling back to the default bitmap dimension when it is absent,
    /// malformed or zero (a zero-area canvas has no natural size to size the box from).
    /// </summary>
    private static float CanvasBitmapDimension(CssBox box, string attribute, float fallback)
    {
        var raw = box.HtmlTag?.TryGetAttribute(attribute);

        return !string.IsNullOrWhiteSpace(raw)
               && uint.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint bitmap)
               && bitmap > 0
            ? bitmap
            : fallback;
    }

    /// <summary>
    /// Reads a numeric <c>width</c>/<c>height</c> presentation attribute as a pixel length, falling back to
    /// <paramref name="fallback"/> when the attribute is absent or non-numeric (percentages and other units
    /// are left to CSS).
    /// </summary>
    private static string PresentationLengthPx(CssBox box, string attribute, string fallback)
    {
        var raw = box.HtmlTag?.TryGetAttribute(attribute);
        return !string.IsNullOrWhiteSpace(raw)
               && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? raw + "px"
            : fallback;
    }

    private const double DefaultProgressTrackLengthPx = 120;

    /// <summary>
    /// HTML §4.10.13/4.10.14: <c>&lt;progress&gt;</c> / <c>&lt;meter&gt;</c> are replaced form controls.
    /// Broiler has no native control chrome, so — matching the bridge's
    /// <c>HtmlPostProcessor.ReplaceProgressLikeWithPlaceholder</c> fallback — a post-cascade pass renders each
    /// as a bordered <c>inline-block</c> track with an absolutely-positioned fill bar proportional to
    /// <c>value</c> (honouring writing-mode / direction for vertical and reversed bars) and hides the
    /// element's fallback text. Runs post-cascade so the injected fill box and forced track geometry are not
    /// re-cascaded. Native replacement for the string rewrite; matches its exact colours/sizes so retiring the
    /// fallback (once the pointer is bumped) does not change rendering.
    /// </summary>
    private static void CorrectProgressBoxes(CssBox box, Uri baseUrl)
    {
        if (box.HtmlTag != null
            && (box.HtmlTag.Name.Equals("progress", StringComparison.OrdinalIgnoreCase)
                || box.HtmlTag.Name.Equals("meter", StringComparison.OrdinalIgnoreCase)))
        {
            bool isMeter = box.HtmlTag.Name.Equals("meter", StringComparison.OrdinalIgnoreCase);
            bool vertical = box.WritingMode != null
                && (box.WritingMode.StartsWith("vertical", StringComparison.OrdinalIgnoreCase)
                    || box.WritingMode.StartsWith("sideways", StringComparison.OrdinalIgnoreCase));
            bool reverseInline = string.Equals(box.Direction, "rtl", StringComparison.OrdinalIgnoreCase);
            double ratio = ResolveProgressValueRatio(box, isMeter);

            // Track (host) box — forced geometry/appearance, matching the string fallback.
            box.Display = CssConstants.InlineBlock;
            box.BoxSizing = "border-box";
            box.Position = CssConstants.Relative;
            box.Overflow = CssConstants.Hidden;
            box.PaddingLeft = box.PaddingRight = box.PaddingTop = box.PaddingBottom = "0";
            SetUniformBorder(box, "1px", "solid", "#767676");
            box.BackgroundColor = isMeter ? "#e6e6e6" : "#f0f0f0";
            box.VerticalAlign = "middle";
            box.Width = vertical ? "16px" : "120px";
            box.Height = vertical ? "120px" : "16px";

            // The element's fallback text/content does not paint; the fill bar replaces it.
            foreach (var child in box.Boxes)
                child.Display = CssConstants.None;

            // Fill bar — absolutely positioned within the relative track, sized to the value ratio.
            var fillExtent = (DefaultProgressTrackLengthPx * ratio)
                .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
            var fill = CssBoxHelper.CreateBlock(box, baseUrl);
            fill.Position = CssConstants.Absolute;
            fill.BackgroundColor = isMeter ? "#4caf50" : "#0a84ff";
            if (vertical)
            {
                fill.Left = "0";
                fill.Right = "0";
                if (reverseInline) fill.Bottom = "0"; else fill.Top = "0";
                fill.Height = fillExtent;
            }
            else
            {
                fill.Top = "0";
                fill.Bottom = "0";
                if (reverseInline) fill.Right = "0"; else fill.Left = "0";
                fill.Width = fillExtent;
            }
            return;
        }

        foreach (var child in box.Boxes)
            CorrectProgressBoxes(child, baseUrl);
    }

    private static void SetUniformBorder(CssBox box, string width, string style, string color)
    {
        box.BorderLeftWidth = box.BorderRightWidth = box.BorderTopWidth = box.BorderBottomWidth = width;
        box.BorderLeftStyle = box.BorderRightStyle = box.BorderTopStyle = box.BorderBottomStyle = style;
        box.BorderLeftColor = box.BorderRightColor = box.BorderTopColor = box.BorderBottomColor = color;
    }

    /// <summary>
    /// Resolves a <c>&lt;progress&gt;</c>/<c>&lt;meter&gt;</c> fill ratio in [0,1] from its numeric
    /// <c>value</c>/<c>max</c> (and, for <c>&lt;meter&gt;</c>, <c>min</c>) attributes, mirroring the fallback.
    /// </summary>
    private static double ResolveProgressValueRatio(CssBox box, bool isMeter)
    {
        double min = isMeter ? ReadNumericAttribute(box, "min", 0) : 0;
        double max = ReadNumericAttribute(box, "max", 1);
        if (max <= min)
            max = min + 1;
        double value = ReadNumericAttribute(box, "value", min);
        return Math.Clamp((value - min) / (max - min), 0, 1);
    }

    private static double ReadNumericAttribute(CssBox box, string name, double fallback)
    {
        var raw = box.HtmlTag?.TryGetAttribute(name);
        return !string.IsNullOrWhiteSpace(raw)
               && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }

    private const int SelectMultipleDefaultVisibleTracks = 4;
    private const int SelectMultipleTrackThicknessPx = 16;
    private const int SelectMultipleInlineExtentPx = 72;
    private const int SelectMultipleChromeThicknessPx = 10;

    /// <summary>
    /// HTML §4.10.7: a <c>&lt;select multiple&gt;</c> is a replaced list-box control. Broiler has no native
    /// control chrome, so — matching the bridge's <c>HtmlPostProcessor.ReplaceSelectMultipleWithPlaceholder</c>
    /// fallback (native-appearance case) — a post-cascade pass renders it as an <c>inline-block</c> list box
    /// with one row track per visible option (the first row painted as the selection highlight, the rest
    /// alternating), an edge scrollbar-chrome strip, honouring <c>writing-mode</c> for vertical/reversed
    /// boxes, and hides the real <c>&lt;option&gt;</c> children. Runs post-cascade (absolute row/chrome boxes
    /// are laid out because the engine discovers absolutes by walking the tree at layout time).
    /// </summary>
    /// <remarks>
    /// Both appearance modes are covered: <c>appearance:auto</c> (native — chrome strip, grey field) and
    /// <c>appearance:none</c> (no chrome, white field, lighter border), branched on the box's cascaded
    /// <c>Appearance</c> value.
    /// </remarks>
    private static void CorrectSelectMultipleBoxes(CssBox box, Uri baseUrl)
    {
        if (box.HtmlTag != null
            && box.HtmlTag.Name.Equals("select", StringComparison.OrdinalIgnoreCase)
            && box.HtmlTag.HasAttribute("multiple"))
        {
            bool vertical = box.WritingMode != null
                && (box.WritingMode.StartsWith("vertical", StringComparison.OrdinalIgnoreCase)
                    || box.WritingMode.StartsWith("sideways", StringComparison.OrdinalIgnoreCase));
            bool reverseBlock = box.WritingMode != null
                && box.WritingMode.EndsWith("-rl", StringComparison.OrdinalIgnoreCase);

            // 'appearance:none' opts out of the native list-box chrome (no scrollbar strip, white field,
            // lighter border) — CSS Basic UI; the box's Appearance is populated by the cascade.
            bool nativeAppearance = !string.Equals(box.Appearance, "none", StringComparison.OrdinalIgnoreCase);

            int visibleTracks = (int)Math.Clamp(
                ReadNumericAttribute(box, "size", SelectMultipleDefaultVisibleTracks), 2, 8);

            int chromeInset = nativeAppearance ? SelectMultipleChromeThicknessPx : 2;
            int blockExtent = (visibleTracks * SelectMultipleTrackThicknessPx) + 4;
            int hostWidth = vertical ? blockExtent : SelectMultipleInlineExtentPx;
            int hostHeight = vertical ? SelectMultipleInlineExtentPx : blockExtent;
            int contentWidth = vertical
                ? visibleTracks * SelectMultipleTrackThicknessPx
                : SelectMultipleInlineExtentPx - chromeInset;
            int contentHeight = vertical
                ? SelectMultipleInlineExtentPx - chromeInset
                : visibleTracks * SelectMultipleTrackThicknessPx;

            // Host (list box) — forced geometry/appearance, matching the string fallback.
            box.Display = CssConstants.InlineBlock;
            box.Position = CssConstants.Relative;
            box.BoxSizing = "border-box";
            box.Overflow = CssConstants.Hidden;
            box.VerticalAlign = "middle";
            box.FontSize = "13px";
            box.FontFamily = "sans-serif";
            box.Width = hostWidth + "px";
            box.Height = hostHeight + "px";
            SetUniformBorder(box, "1px", "solid", nativeAppearance ? "#767676" : "#9a9a9a");
            box.BackgroundColor = nativeAppearance ? "#f0f0f0" : "#ffffff";

            // The real <option> children do not paint; the row tracks replace them.
            foreach (var child in box.Boxes)
                child.Display = CssConstants.None;

            for (int i = 0; i < visibleTracks; i++)
            {
                var background = i == 0 ? "#3875d7" : (i % 2 == 0 ? "#ffffff" : "#f7f7f7");
                int offset = 1 + (i * SelectMultipleTrackThicknessPx);
                var track = CssBoxHelper.CreateBlock(box, baseUrl);
                track.Position = CssConstants.Absolute;
                track.BackgroundColor = background;
                if (vertical)
                {
                    track.Top = "1px";
                    if (reverseBlock) track.Right = offset + "px"; else track.Left = offset + "px";
                    track.Width = SelectMultipleTrackThicknessPx + "px";
                    track.Height = Math.Max(contentHeight, 8) + "px";
                }
                else
                {
                    track.Left = "1px";
                    track.Top = offset + "px";
                    track.Width = Math.Max(contentWidth, 8) + "px";
                    track.Height = SelectMultipleTrackThicknessPx + "px";
                    track.BorderBottomWidth = "1px";
                    track.BorderBottomStyle = "solid";
                    track.BorderBottomColor = "#d0d0d0";
                }
            }

            // Scrollbar-chrome strip along the block-end edge — native appearance only.
            if (nativeAppearance)
            {
                var chrome = CssBoxHelper.CreateBlock(box, baseUrl);
                chrome.Position = CssConstants.Absolute;
                chrome.BackgroundColor = "#dcdcdc";
                if (vertical)
                {
                    chrome.Left = "1px";
                    if (reverseBlock) chrome.Top = "1px"; else chrome.Bottom = "1px";
                    chrome.Width = (hostWidth - 2) + "px";
                    chrome.Height = (SelectMultipleChromeThicknessPx - 2) + "px";
                    chrome.BorderTopWidth = "1px";
                    chrome.BorderTopStyle = "solid";
                    chrome.BorderTopColor = "#b8b8b8";
                }
                else
                {
                    chrome.Top = "1px";
                    chrome.Right = "1px";
                    chrome.Width = (SelectMultipleChromeThicknessPx - 2) + "px";
                    chrome.Height = (hostHeight - 2) + "px";
                    chrome.BorderLeftWidth = "1px";
                    chrome.BorderLeftStyle = "solid";
                    chrome.BorderLeftColor = "#b8b8b8";
                }
            }
            return;
        }

        foreach (var child in box.Boxes)
            CorrectSelectMultipleBoxes(child, baseUrl);
    }

    private static void CorrectFramesetBoxes(CssBox box)
    {
        bool isFrameset = box.HtmlTag != null
            && box.HtmlTag.Name.Equals("frameset", StringComparison.OrdinalIgnoreCase);
        bool parentIsFrameset = box.ParentBox?.HtmlTag != null
            && box.ParentBox.HtmlTag.Name.Equals("frameset", StringComparison.OrdinalIgnoreCase);

        if (isFrameset)
        {
            if (!parentIsFrameset)
            {
                // Outermost frameset: fill the viewport, overriding any inherited
                // body margin.  Fixed positioning resolves 100%/offsets against the
                // initial containing block (the viewport).
                box.Position = CssConstants.Fixed;
                box.Left = "0";
                box.Top = "0";
                box.Width = "100%";
                box.Height = "100%";
                box.MarginLeft = box.MarginTop = box.MarginRight = box.MarginBottom = "0";
            }
            LayoutFramesetChildren(box);
        }

        foreach (var child in box.Boxes)
            CorrectFramesetBoxes(child);
    }

    private static void LayoutFramesetChildren(CssBox frameset)
    {
        // Cells are <frame> and nested <frameset> children; everything else
        // (<noframes>, stray text) is fallback and must not paint.
        var cells = new List<CssBox>();
        foreach (var child in frameset.Boxes)
        {
            string name = child.HtmlTag?.Name;
            if (string.Equals(name, "frame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "frameset", StringComparison.OrdinalIgnoreCase))
                cells.Add(child);
            else if (string.Equals(name, "noframes", StringComparison.OrdinalIgnoreCase))
                child.Display = CssConstants.None;
        }

        if (cells.Count == 0)
            return;

        // cols → columns, rows → rows; missing dimension is a single track.
        var colPercents = ParseFramesetSpec(frameset.GetAttribute("cols"), nominalTotal: 1024);
        var rowPercents = ParseFramesetSpec(frameset.GetAttribute("rows"), nominalTotal: 768);
        if (colPercents.Count == 0) colPercents = [100.0];
        if (rowPercents.Count == 0) rowPercents = [100.0];

        // Cells fill the grid row-major (HTML frameset layout order).
        int cols = colPercents.Count;
        int rows = rowPercents.Count;

        double[] colLeft = new double[cols];
        for (int c = 1; c < cols; c++)
            colLeft[c] = colLeft[c - 1] + colPercents[c - 1];
        double[] rowTop = new double[rows];
        for (int r = 1; r < rows; r++)
            rowTop[r] = rowTop[r - 1] + rowPercents[r - 1];

        for (int i = 0; i < cells.Count; i++)
        {
            int r = i / cols;
            int c = i % cols;
            if (r >= rows)
                break; // more frames than cells — extras are not rendered

            var cell = cells[i];
            cell.Position = CssConstants.Absolute;
            cell.Left = FormatPercent(colLeft[c]);
            cell.Top = FormatPercent(rowTop[r]);
            cell.Width = FormatPercent(colPercents[c]);
            cell.Height = FormatPercent(rowPercents[r]);
            cell.MarginLeft = cell.MarginTop = cell.MarginRight = cell.MarginBottom = "0";
        }
    }

    private static string FormatPercent(double value) =>
        value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Parses a frameset <c>cols</c>/<c>rows</c> spec (comma-separated
    /// <c>*</c> / <c>N*</c> / <c>N%</c> / <c>N</c>) into per-track percentages of
    /// the frameset that sum to ~100.  Pixel tracks are resolved against
    /// <paramref name="nominalTotal"/> (the viewport axis) since the final layout
    /// is expressed in percentages.
    /// </summary>
    private static List<double> ParseFramesetSpec(string spec, double nominalTotal)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(spec))
            return result;

        var entries = spec.Split(',');
        var kinds = new char[entries.Length];   // '*', '%', or 'p' (pixel)
        var values = new double[entries.Length];
        double reserved = 0;   // fraction of total consumed by fixed/percent tracks
        double starWeight = 0;

        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i].Trim();
            if (e.Length == 0 || e == "*")
            {
                kinds[i] = '*';
                values[i] = 1;
                starWeight += 1;
            }
            else if (e.EndsWith("*", StringComparison.Ordinal))
            {
                kinds[i] = '*';
                values[i] = double.TryParse(e[..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var w) && w > 0 ? w : 1;
                starWeight += values[i];
            }
            else if (e.EndsWith("%", StringComparison.Ordinal)
                && double.TryParse(e[..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                kinds[i] = '%';
                values[i] = Math.Max(0, pct);
                reserved += values[i] / 100.0;
            }
            else if (double.TryParse(e.TrimEnd('p', 'x', 'P', 'X'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var px))
            {
                kinds[i] = 'p';
                values[i] = Math.Max(0, px);
                reserved += nominalTotal > 0 ? values[i] / nominalTotal : 0;
            }
            else
            {
                kinds[i] = '*';
                values[i] = 1;
                starWeight += 1;
            }
        }

        double remaining = Math.Max(0, 1.0 - reserved);
        for (int i = 0; i < entries.Length; i++)
        {
            double frac = kinds[i] switch
            {
                '%' => values[i] / 100.0,
                'p' => nominalTotal > 0 ? values[i] / nominalTotal : 0,
                _ => starWeight > 0 ? remaining * (values[i] / starWeight) : remaining,
            };
            result.Add(frac * 100.0);
        }

        // Fixed/percentage tracks can request more than the frameset's area
        // (e.g. rows="4294967227%,*"): the HTML frameset algorithm scales the
        // tracks down proportionally so they fit exactly, never overflowing the
        // frameset.  Without this a single giant track produces a frame sized in
        // billions of pixels, overflowing the embedded-document bitmap allocation.
        double total = 0;
        foreach (var p in result)
            total += p;
        if (total > 100.0)
        {
            double scale = 100.0 / total;
            for (int i = 0; i < result.Count; i++)
                result[i] *= scale;
        }

        return result;
    }

    private static void CorrectObjectBoxes(CssBox box)
    {
        for (int i = box.Boxes.Count - 1; i >= 0; i--)
        {
            var childBox = box.Boxes[i];

            if (childBox is CssBoxImage &&
                childBox.HtmlTag != null &&
                childBox.HtmlTag.Name.Equals("object", StringComparison.OrdinalIgnoreCase))
            {
                // This <object> was promoted to CssBoxImage because its data
                // attribute contains a data:image URI.  Remove fallback children
                // so only the image renders.
                childBox.Boxes.Clear();
            }

            // Recurse into all children (including non-object boxes)
            CorrectObjectBoxes(childBox);
        }
    }

    private static void CorrectLineBreaksBlocks(CssBox box, ref bool followingBlock)
    {
        followingBlock = followingBlock || box.IsBlock;

        // The <br> scan below recomputes followingBlock from the siblings that
        // precede each <br>, but a <br> at index 0 has no preceding sibling, so
        // it must fall back to this box's content-start context — a <br> at the
        // very start of a block generates a full empty line. Capture that value
        // now, before the recursive child walk mutates followingBlock to the
        // trailing-content state (which otherwise leaks in and suppresses the
        // empty line of a *leading* <br>, collapsing consecutive <br><br> to a
        // single line advance).
        bool entryFollowingBlock = followingBlock;

        foreach (var childBox in box.Boxes)
        {
            // CSS2.1 §9.6: Out-of-flow positioned elements do not participate
            // in the in-flow block/inline sequence that governs <br> heights.
            // Process their subtrees with an isolated state so a block-level
            // absolutely-positioned element does not make a following <br>
            // generate a spurious empty line.
            if (childBox.Position is CssConstants.Absolute or CssConstants.Fixed)
            {
                bool isolated = false;
                CorrectLineBreaksBlocks(childBox, ref isolated);
                continue;
            }

            CorrectLineBreaksBlocks(childBox, ref followingBlock);
            // CSS2.1 §9.2.1/§10.8: An atomic inline-level box (inline-block,
            // inline-flex, inline-grid) is *inline* content even though it
            // carries no text words — it sits on the current line, so a
            // following <br> merely ends that line rather than producing a
            // spurious empty line.  Treat it like text (clears followingBlock)
            // so it is not mistaken for block-level content below.
            followingBlock = !IsAtomicInlineLevel(childBox)
                && childBox.Words.Count == 0
                && (followingBlock || childBox.IsBlock);
        }

        int lastBr = -1;
        CssBox brBox;

        do
        {
            // Re-scan from the block's content-start context each pass so the
            // run preceding *this* <br> is measured fresh; otherwise the value
            // left over from the previous <br> (or the child walk) misclassifies
            // a leading <br>.
            followingBlock = entryFollowingBlock;
            brBox = null;
            for (int i = 0; i < box.Boxes.Count && brBox == null; i++)
            {
                if (i > lastBr && box.Boxes[i].IsBrElement)
                {
                    brBox = box.Boxes[i];
                    lastBr = i;
                }
                else if (box.Boxes[i].Position is CssConstants.Absolute or CssConstants.Fixed)
                {
                    // Out-of-flow: transparent to the in-flow block/inline run.
                }
                else if (box.Boxes[i].Words.Count > 0 || IsAtomicInlineLevel(box.Boxes[i]))
                {
                    followingBlock = false;
                }
                else if (box.Boxes[i].IsBlock)
                {
                    followingBlock = true;
                }
            }

            if (brBox != null)
            {
                brBox.Display = CssConstants.Block;
                if (followingBlock)
                    brBox.Height = ".95em"; // TODO:a check the height to min-height when it is supported
            }
        } while (brBox != null);
    }

    /// <summary>
    /// Returns whether <paramref name="box"/> is an atomic inline-level box
    /// (<c>inline-block</c>, <c>inline-flex</c>, or <c>inline-grid</c>).  Such a
    /// box participates in the inline formatting context — it occupies the
    /// current line — so for <c>&lt;br&gt;</c> empty-line accounting it counts
    /// as inline content, not as a preceding block.
    /// </summary>
    private static bool IsAtomicInlineLevel(CssBox box)
        => box.Display == CssConstants.InlineBlock
           || box.Display == "inline-flex"
           || box.Display == "inline-grid";

    private static void CorrectBlockInsideInline(CssBox box, Uri baseUrl)
    {
        try
        {
            // CSS Flexbox §4 / CSS Grid §7: All direct children of a
            // flex/grid container become flex/grid items — they must NOT
            // be rearranged by the block-inside-inline correction (which
            // wraps block children in anonymous boxes and merges them).
            //
            // CSS2.1 §9.2.1.1 / §10.3.9: Inline-block boxes establish a
            // new block formatting context for their children.  Block-level
            // children inside an inline-block are valid and must NOT be
            // split out by the block-inside-inline correction.  Without
            // this skip, <span style="display:inline-block"> containing a
            // <span style="display:block"> is incorrectly unwrapped,
            // causing the block child to expand to the full container width
            // instead of being constrained by the inline-block's
            // shrink-to-fit sizing (e.g. Google.de button wrappers).
            if (box.Display is "flex" or "inline-flex" or "grid" or "inline-grid"
                or CssConstants.InlineBlock)
            {
                // Still recurse into children — the children themselves
                // may contain nested inline contexts that need correction.
                foreach (var childBox in box.Boxes)
                    CorrectBlockInsideInline(childBox, baseUrl);
                return;
            }

            if (LayoutBoxUtils.ContainsInlinesOnly(box) && !ContainsInlinesOnlyDeep(box))
            {
                var tempRightBox = CorrectBlockInsideInlineImp(box, baseUrl);
                while (tempRightBox != null)
                {
                    // loop on the created temp right box for the fixed box until no more need (optimization remove recursion)
                    CssBox newTempRightBox = null;
                    if (LayoutBoxUtils.ContainsInlinesOnly(tempRightBox) && !ContainsInlinesOnlyDeep(tempRightBox))
                        newTempRightBox = CorrectBlockInsideInlineImp(tempRightBox, baseUrl);

                    tempRightBox.ParentBox.SetAllBoxes(tempRightBox);
                    tempRightBox.ParentBox = null;
                    tempRightBox = newTempRightBox;
                }
            }

            if (!LayoutBoxUtils.ContainsInlinesOnly(box))
            {
                foreach (var childBox in box.Boxes)
                    CorrectBlockInsideInline(childBox, baseUrl);
            }
            else
            {
                // A box whose only children are floats reaches here: ContainsInlinesOnly counts
                // a float as inline-compatible (CSS2.1 §9.5) so it answers true, and
                // ContainsInlinesOnlyDeep skips floats outright so the split never fires either
                // -- the float's whole subtree was left uncorrected. A float establishes its own
                // block formatting context, so correct it on its own. Acid1's <form> lives
                // inside a floated <li> exactly like this, and the two block <p>s inside that
                // inline form never got their anonymous blocks: the form's subtree -- both
                // radio-button lines -- laid out at zero size and painted nothing.
                //
                // Absolutely positioned children are deliberately NOT descended into: both
                // ContainsInlinesOnlyDeep and ContainsVariantBoxes treat them as transparent to
                // the inline context (their static position is resolved during inline layout),
                // and correcting inside one changes the inline bounding box the anchor pass
                // reads as its containing block.
                var floats = new List<CssBox>();
                foreach (var childBox in box.Boxes)
                {
                    if (childBox.Float != CssConstants.None)
                        floats.Add(childBox);
                }

                foreach (var childBox in floats)
                    CorrectBlockInsideInline(childBox, baseUrl);
            }
        }
        catch (Exception ex)
        {
            ((IHtmlContainerInt)box.ContainerInt).ReportError(HtmlRenderErrorType.HtmlParsing, "Failed in block inside inline box correction", ex);
        }
    }

    /// <summary>
    /// Rearrange the DOM of the box to have block box with boxes before the inner block box and after.
    /// </summary>
    /// <param name="box">the box that has the problem</param>
    private static CssBox CorrectBlockInsideInlineImp(CssBox box, Uri baseUrl)
    {
        // CSS2.1 §9.2.1.1: When an inline element contains a block-level
        // child, the inline is broken around the block into anonymous block
        // boxes.  If the inline had position:relative/absolute/fixed (i.e.
        // established a containing block for absolutely-positioned descendants),
        // the hoisted blocks lose their parent–child relationship in the box
        // tree.  Record the original positioned ancestor so that
        // FindPositionedContainingBlock() can still find it.
        bool wasPositioned = box.Position is CssConstants.Relative or CssConstants.Absolute or CssConstants.Fixed;
        // Also inherit any split-positioned-ancestor from a higher level.
        CssBox splitAncestor = wasPositioned ? box : box.SplitPositionedAncestor;
        if (box.Display == CssConstants.Inline)
        {
            box.Display = CssConstants.Block;
            // The element is still inline as far as CSS is concerned; the block-level display is
            // an artefact of how this split is modelled. Layout needs to know, because a box like
            // this is not a containing block a percentage resolves against.
            box.IsBlockifiedInlineSplit = true;
        }

        if (box.Boxes.Count > 1 || box.Boxes[0].Boxes.Count > 1)
        {
            var leftBlock = CssBoxHelper.CreateBlock(box, baseUrl);

            // Gather the leading inline-only run into leftBlock, stopping at the
            // first child that still contains a block (the split point). Never
            // fold leftBlock (appended last, so it becomes box.Boxes[0] only once
            // every real child is consumed) into itself: that would detach the
            // whole subtree and leave box.Boxes empty.
            // CSS2.1 §9.2.1.1/§10.3.9: fold atomic inlines (inline-block/-flex/-grid) into
            // the leading run too — they are inline-level content that establishes its own
            // BFC, so they must not be mis-selected as the block to split around (which dissolved
            // the inline-block and dropped its box when it had a display:none sibling).
            while (box.Boxes[0] != leftBlock
                   && (IsAtomicInlineLevel(box.Boxes[0]) || ContainsInlinesOnlyDeep(box.Boxes[0])))
                box.Boxes[0].ParentBox = leftBlock;

            // If every child folded into leftBlock (only leftBlock remains) there
            // is no distinct block to split around — box.Boxes[1] below would throw
            // ArgumentOutOfRangeException (the reported crash). This happens when
            // the only "block" that made box fail !ContainsInlinesOnlyDeep is inside
            // an out-of-flow (float/abspos) descendant, which ContainsInlinesOnlyDeep
            // skips: every child reads as inline-only-deep and folds in, so no
            // in-flow block actually needs hoisting. Undo the fold — move the
            // children back onto box and drop leftBlock — so box stays
            // ContainsInlinesOnly and the caller's `if (!ContainsInlinesOnly(box))`
            // recursion skips it. Returning with box == [leftBlock] would instead
            // make that recursion re-enter this same collapse and re-wrap forever
            // (stack overflow).
            if (box.Boxes.Count <= 1)
            {
                if (leftBlock.Boxes.Count > 0)
                    box.SetAllBoxes(leftBlock);
                leftBlock.ParentBox = null;
                return null;
            }

            leftBlock.SetBeforeBox(box.Boxes[0]);

            var splitBox = box.Boxes[1];
            splitBox.ParentBox = null;

            CorrectBlockSplitBadBoxCore(box, splitBox, leftBlock, baseUrl, splitAncestor);

            // remove block that did not get any inner elements
            if (leftBlock.Boxes.Count < 1)
                leftBlock.ParentBox = null;

            // Propagate the positioned ancestor link to hoisted children.
            if (splitAncestor != null)
            {
                foreach (var child in box.Boxes)
                    PropagateSplitPositionedAncestor(child, splitAncestor);
            }

            int minBoxes = leftBlock.ParentBox != null ? 2 : 1;
            if (box.Boxes.Count > minBoxes)
            {
                // create temp box to handle the tail elements and then get them back so no deep hierarchy is created
                var tempRightBox = CssBoxHelper.CreateBox(box, baseUrl, null, box.Boxes[minBoxes]);
                while (box.Boxes.Count > minBoxes + 1)
                    box.Boxes[minBoxes + 1].ParentBox = tempRightBox;

                if (splitAncestor != null)
                    PropagateSplitPositionedAncestor(tempRightBox, splitAncestor);

                return tempRightBox;
            }
        }
        else if (box.Boxes[0].Display == CssConstants.Inline)
        {
            box.Boxes[0].Display = CssConstants.Block;
            box.Boxes[0].IsBlockifiedInlineSplit = true;
        }

        return null;
    }

    /// <summary>
    /// Recursively propagate <see cref="CssBox.SplitPositionedAncestor"/>
    /// down through a subtree that was hoisted out of a positioned inline
    /// by the block-inside-inline correction.  Stops recursing when it
    /// reaches a box that already has its own positioned role.
    /// </summary>
    private static void PropagateSplitPositionedAncestor(CssBox box, CssBox ancestor)
    {
        // Don't override if the box itself is positioned — it forms its
        // own containing block.
        if (box.Position is CssConstants.Relative or CssConstants.Absolute or CssConstants.Fixed)
            return;

        box.SplitPositionedAncestor ??= ancestor;

        foreach (var child in box.Boxes)
            PropagateSplitPositionedAncestor(child, ancestor);
    }

    /// <summary>
    /// Core implementation of block-inside-inline split.  <paramref name="posAncestor"/>
    /// tracks the closest positioned ancestor that was stripped away during
    /// recursive splitting so that hoisted blocks can carry a
    /// <see cref="CssBox.SplitPositionedAncestor"/> reference.
    /// </summary>
    private static void CorrectBlockSplitBadBoxCore(CssBox parentBox, CssBox badBox, CssBox leftBlock, Uri baseUrl, CssBox posAncestor)
    {
        // If the box being split is positioned, it becomes the reference
        // for any blocks hoisted out of its subtree.
        if (badBox.Position is CssConstants.Relative or CssConstants.Absolute or CssConstants.Fixed)
            posAncestor = badBox;

        CssBox leftbox = null;
        while (badBox.Boxes[0].IsInline && ContainsInlinesOnlyDeep(badBox.Boxes[0]))
        {
            if (leftbox == null)
            {
                // if there is no elements in the left box there is no reason to keep it
                leftbox = CssBoxHelper.CreateBox(leftBlock, baseUrl, badBox.HtmlTag);
                leftbox.InheritStyle(badBox, true);
            }
            badBox.Boxes[0].ParentBox = leftbox;
        }

        // If badBox is the positioned ancestor being split, register the
        // left-side copy as a fragment so GetInlineBoundingBox can find it.
        if (leftbox != null && posAncestor == badBox)
            posAncestor.AddSplitFragment(leftbox);

        var splitBox = badBox.Boxes[0];
        if (!ContainsInlinesOnlyDeep(splitBox))
        {
            CorrectBlockSplitBadBoxCore(parentBox, splitBox, leftBlock, baseUrl, posAncestor);
            splitBox.ParentBox = null;
        }
        else
        {
            splitBox.ParentBox = parentBox;
            // The block being hoisted to parentBox was originally a
            // descendant of a positioned inline.  Record the link.
            if (posAncestor != null)
                SetSplitAncestorDeep(splitBox, posAncestor);
        }

        if (badBox.Boxes.Count > 0)
        {
            CssBox rightBox;
            if (splitBox.ParentBox != null || parentBox.Boxes.Count < 3)
            {
                rightBox = CssBoxHelper.CreateBox(parentBox, baseUrl, badBox.HtmlTag);
                rightBox.InheritStyle(badBox, true);

                if (parentBox.Boxes.Count > 2)
                    rightBox.SetBeforeBox(parentBox.Boxes[1]);

                if (splitBox.ParentBox != null)
                    splitBox.SetBeforeBox(rightBox);
            }
            else
            {
                rightBox = parentBox.Boxes[2];
            }

            rightBox.SetAllBoxes(badBox);

            // Register the right-side copy as a fragment of the
            // positioned ancestor so GetInlineBoundingBox includes it.
            if (posAncestor == badBox)
                posAncestor.AddSplitFragment(rightBox);

            // Also tag the right-side anonymous block.
            if (posAncestor != null)
                SetSplitAncestorDeep(rightBox, posAncestor);
        }
        // CSS2.1 §9.2.1.1: breaking an inline box around a block replaces it with copies of
        // itself on either side of the block — which is what leftbox and rightBox above are.
        // When the block was the inline box's *only* content there is nothing on either side
        // and neither copy is made, and the element was then left with no box at all: it is
        // detached from its parent before the split and nothing re-attaches it. It still exists
        // in the document, and everything keyed to the element rather than to its content still
        // needs a box to read. A <body> broken this way is the canvas background's only source
        // (CSS Backgrounds §2.11.2), so `<body style="display:inline;background:green">` holding
        // one <p> rendered a white page instead of a green one.
        if (leftbox == null && badBox.Boxes.Count == 0 && badBox.HtmlTag != null
            && splitBox.ParentBox != null)
        {
            var emptyCopy = CssBoxHelper.CreateBox(leftBlock, baseUrl, badBox.HtmlTag);
            emptyCopy.InheritStyle(badBox, true);
        }

        else if (splitBox.ParentBox != null && parentBox.Boxes.Count > 1)
        {
            splitBox.SetBeforeBox(parentBox.Boxes[1]);
            if (splitBox.HtmlTag != null && splitBox.HtmlTag.Name == "br" && (leftbox != null || leftBlock.Boxes.Count > 1))
                splitBox.Display = CssConstants.Inline;
        }
    }

    /// <summary>
    /// Set <see cref="CssBox.SplitPositionedAncestor"/> on a box and all
    /// its descendants, stopping at boxes that are themselves positioned.
    /// </summary>
    private static void SetSplitAncestorDeep(CssBox box, CssBox ancestor)
    {
        if (box.Position is CssConstants.Relative or CssConstants.Absolute or CssConstants.Fixed)
            return;
        box.SplitPositionedAncestor ??= ancestor;
        foreach (var child in box.Boxes)
            SetSplitAncestorDeep(child, ancestor);
    }

    private static void CorrectInlineBoxesParent(CssBox box, Uri baseUrl)
    {
        // CSS Flexbox §4 / CSS Grid §7: All direct children of a
        // flex/grid container are flex/grid items — do not wrap inline
        // children in anonymous block boxes.
        //
        // CSS2.1 §9.2.1.1 / §10.3.9: Inline-block boxes establish a new
        // block formatting context — their children (block or inline) are
        // laid out internally and must not be rearranged by this
        // correction.
        if (box.Display is not ("flex" or "inline-flex" or "grid" or "inline-grid"
                or CssConstants.InlineBlock)
            && ContainsVariantBoxes(box))
        {
            for (int i = 0; i < box.Boxes.Count; i++)
            {
                if (box.Boxes[i].IsInline)
                {
                    var newbox = CssBoxHelper.CreateBlock(box, baseUrl, null, box.Boxes[i++]);
                    while (i < box.Boxes.Count && box.Boxes[i].IsInline)
                        box.Boxes[i].ParentBox = newbox;
                }
            }
        }

        if (!LayoutBoxUtils.ContainsInlinesOnly(box))
        {
            foreach (var childBox in box.Boxes)
                CorrectInlineBoxesParent(childBox, baseUrl);
        }
        else
        {
            // ContainsInlinesOnly counts a float as inline-compatible (CSS2.1 §9.5), so a box
            // whose children are all floats answers true and the recursion above skipped its
            // whole subtree. A float establishes its own block formatting context and needs the
            // same correction as any other block, so descend into it. Acid1's <ul> holds nothing
            // but floated <li>s, which is why the <form> two levels down never had its inline
            // run wrapped -- and, without that wrapper, never reached the block-inside-inline
            // split either, so both radio-button lines laid out at zero size. Absolutely
            // positioned children stay untouched, for the reason given in
            // CorrectBlockInsideInline's matching branch.
            var floats = new List<CssBox>();
            foreach (var childBox in box.Boxes)
            {
                if (childBox.Float != CssConstants.None)
                    floats.Add(childBox);
            }

            foreach (var childBox in floats)
                CorrectInlineBoxesParent(childBox, baseUrl);
        }
    }

    private static bool ContainsInlinesOnlyDeep(CssBox box)
    {
        foreach (var childBox in box.Boxes)
        {
            // CSS2.1 §9.5: Floats are out-of-flow and should not trigger
            // block-inside-inline corrections.  Skip them when checking
            // whether a box contains only inline content.
            if (childBox.Float != CssConstants.None)
                continue;

            // CSS2.1 §9.6: Absolutely and fixed positioned elements are also
            // out of flow — they are blockified (§9.7) but, like floats, do
            // not break the surrounding inline formatting context.  Their
            // static position is resolved during inline layout, so they must
            // not trigger the block-inside-inline correction either.
            if (childBox.Position is CssConstants.Absolute or CssConstants.Fixed)
                continue;

            // CSS2.1 §9.2.4: a display:none element generates no box at all, so it can no
            // more make its parent "block inside inline" than an absent element could. It was
            // counted as block-level here (only the inline-* displays answer IsInline), which
            // is the one place this predicate disagreed with LayoutBoxUtils.ContainsInlinesOnly
            // and ContainsVariantBoxes, both of which already skip it. The split it triggered
            // then dissolved the hidden subtree into the surrounding flow and made it visible:
            // on www.mediawiki.org the skin's no-JS search form and its collapsed "Appearance"
            // menu, each display:none beside inline content, were laid out full-size at the top
            // of the page and pushed the whole article down.
            if (childBox.Display == CssConstants.None)
                continue;

            if (!childBox.IsInline)
                return false;

            // CSS2.1 §9.2.1.1 / §10.3.9: Inline-block boxes establish a
            // new block formatting context.  Their block-level children are
            // contained within the inline-block and do NOT constitute
            // "block inside inline" at the outer level.  Stop recursing
            // into inline-block children so that, e.g., <span display:
            // inline-block> containing <span display:block> does not
            // trigger the block-inside-inline correction on the parent.
            //
            // Same applies to flex/grid containers — their children are
            // laid out internally and must not be inspected here.
            if (childBox.Display is CssConstants.InlineBlock
                or "flex" or "inline-flex" or "grid" or "inline-grid")
                continue;

            if (!ContainsInlinesOnlyDeep(childBox))
                return false;
        }

        return true;
    }

    private static bool ContainsVariantBoxes(CssBox box)
    {
        bool hasBlock = false;
        bool hasInline = false;

        for (int i = 0; i < box.Boxes.Count && (!hasBlock || !hasInline); i++)
        {
            // CSS2.1 §9.2.4: A 'display:none' box generates no box at all, so it
            // is transparent to the mixed-content test.  An invisible <style>,
            // <script>, or display:none <span> between inline-level siblings is
            // neither inline nor block ('none') and must not be counted as a
            // block — otherwise a run of inline-blocks separated by such hidden
            // boxes looks "mixed" and gets torn into stacked anonymous blocks
            // (mirrors the skip already in LayoutBoxUtils.ContainsInlinesOnly).
            if (box.Boxes[i].Display == CssConstants.None)
                continue;

            // CSS2.1 §9.5: Floats are out-of-flow — they do not create a
            // mixed inline/block situation that requires anonymous block
            // wrapping.
            if (box.Boxes[i].Float != CssConstants.None)
                continue;
            // CSS2.1 §9.2.4: A 'display:none' box generates no box, so it does
            // not create a mixed inline/block situation and must not trigger
            // anonymous-block wrapping of surrounding inline siblings.
            if (box.Boxes[i].Display == CssConstants.None)
                continue;
            var isBlock = !box.Boxes[i].IsInline;
            hasBlock = hasBlock || isBlock;
            hasInline = hasInline || !isBlock;
        }

        return hasBlock && hasInline;
    }

    /// <summary>
    /// SVG 2 §8.2 ("Establishing a new viewport"): sizes an outer
    /// <c>&lt;svg&gt;</c> as a replaced element.
    /// <para>Its <c>width</c>/<c>height</c> presentation attributes default to
    /// <c>auto</c>, and a valid <c>viewBox</c> gives the element an intrinsic
    /// aspect ratio (but no intrinsic size). CSS then sizes it (CSS2.1 §10.3.2 /
    /// Sizing 4 §4): an <c>auto</c> width resolves to <c>100%</c> of the containing
    /// block and the other axis is transferred through the ratio, so
    /// <c>&lt;svg viewBox="0 0 500 500"&gt;</c> in the body is a viewport-wide
    /// square — not the 300×150 default object size. Broiler sized every such
    /// element 300×150, rendering a small letterboxed drawing where the reference
    /// browser fills the page (WPT <c>inert/inert-svg-hittest</c>,
    /// <c>accessibility/svg-mouse-listener</c>,
    /// <c>svg/animations/svgrect-animation-invalid-value-1</c>).</para>
    /// <para>With no usable ratio — no <c>viewBox</c>, a malformed one, or one with
    /// a non-positive width/height — each auto axis falls back to the CSS default
    /// object size, per axis: <c>&lt;svg width="100"&gt;</c> is 100×150.</para>
    /// <para>The auto height is left for the layout engine to transfer from the
    /// used width (<c>CssBox.TryResolveAspectRatioBlockHeight</c>), because a
    /// <c>100%</c> or percentage width is not known here. The reverse transfer — an
    /// auto width from a definite height — is done directly, and only for a pixel
    /// height, since a percentage height needs the same layout knowledge.</para>
    /// </summary>
    private static void ApplySvgReplacedSizing(CssBox box)
    {
        // CSS wins over the presentation attributes: box.Width/Height are already
        // cascaded, so an attribute only fills in an axis the cascade left auto.
        if (IsAutoLength(box.Width))
            box.Width = SvgDimensionAttribute(box, "width");
        if (IsAutoLength(box.Height))
            box.Height = SvgDimensionAttribute(box, "height");

        bool widthIsAuto = IsAutoLength(box.Width);
        bool heightIsAuto = IsAutoLength(box.Height);

        // CSS Sizing 4 §4: an author `aspect-ratio` is a *preferred* ratio that
        // replaces the natural one the viewBox gives, so it is consulted first and
        // never overwritten.
        bool hasRatio = CssBox.TryParseAspectRatio(box.AspectRatio, out double ratio);
        if (!hasRatio && TryParseSvgViewBoxRatio(box, out ratio))
        {
            hasRatio = true;
            if (heightIsAuto)
                box.AspectRatio = FormatRatio(ratio);
        }

        if (hasRatio && widthIsAuto)
        {
            // A definite height gives the width directly; otherwise the auto width
            // resolves to 100% of the containing block and the layout engine
            // transfers it back into the auto height.
            if (!heightIsAuto && TryParsePixelLength(box.Height, out double heightPx))
                box.Width = FormatPixels(heightPx * ratio);
            else
                box.Width = "100%";

            widthIsAuto = false;
        }

        // With no usable ratio each auto axis falls back to the CSS default object
        // size independently; with one, the auto height is the layout engine's to
        // transfer and must stay auto.
        if (widthIsAuto)
            box.Width = "300px";
        if (heightIsAuto && !hasRatio)
            box.Height = "150px";
    }

    /// <summary>
    /// True when <paramref name="box"/> sits inside another <c>&lt;svg&gt;</c> — i.e. it is a
    /// nested SVG viewport rather than the outermost <c>&lt;svg&gt;</c> the host document lays
    /// out as a replaced element. The distinction matters because the outer element hides every
    /// box beneath it (SVG internals are not CSS-visible here), so a nested <c>&lt;svg&gt;</c>
    /// arrives at the cascade already carrying <c>display: none</c> from its ancestor rather
    /// than from the stylesheet.
    /// </summary>
    private static bool HasSvgAncestor(CssBox box)
    {
        for (var ancestor = box.ParentBox; ancestor != null; ancestor = ancestor.ParentBox)
        {
            if (ancestor.HtmlTag != null &&
                ancestor.HtmlTag.Name.Equals("svg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads an outer <c>&lt;svg&gt;</c>'s <c>width</c>/<c>height</c>
    /// presentation attribute as a CSS length, mapping an absent, empty or
    /// <c>auto</c> value back to <c>auto</c> so the caller's sizing rules apply.
    /// </summary>
    private static string SvgDimensionAttribute(CssBox box, string attribute)
    {
        var raw = box.HtmlTag?.TryGetAttribute(attribute);
        if (string.IsNullOrWhiteSpace(raw))
            return CssConstants.Auto;

        var trimmed = raw.Trim();
        return trimmed.Equals(CssConstants.Auto, StringComparison.OrdinalIgnoreCase)
            ? CssConstants.Auto
            : NormaliseDimensionAttribute(trimmed);
    }

    /// <summary>
    /// SVG 2 §8.2: reads the intrinsic aspect ratio (width ÷ height) an outer
    /// <c>&lt;svg&gt;</c>'s <c>viewBox</c> establishes. The attribute is exactly
    /// four unitless numbers separated by whitespace and/or commas; anything else —
    /// a wrong count, a unit suffix, or a non-positive width or height — leaves the
    /// element with no ratio at all (matching the reference browser, which then
    /// falls back to the default object size).
    /// </summary>
    private static bool TryParseSvgViewBoxRatio(CssBox box, out double ratio)
    {
        ratio = 0;

        var raw = box.HtmlTag?.TryGetAttribute("viewBox");
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split(ViewBoxSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            return false;

        // Only the third and fourth numbers (width, height) shape the ratio, but
        // all four must parse: a malformed origin invalidates the whole attribute.
        Span<double> numbers = stackalloc double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        if (!(numbers[2] > 0) || !(numbers[3] > 0))
            return false;

        ratio = numbers[2] / numbers[3];
        return double.IsFinite(ratio) && ratio > 0;
    }

    private static readonly char[] ViewBoxSeparators = [' ', '\t', '\r', '\n', '\f', ','];

    /// <summary><c>true</c> when a cascaded length property is absent or the
    /// <c>auto</c> keyword — the two forms an unspecified box dimension takes.
    /// </summary>
    private static bool IsAutoLength(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals(CssConstants.Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a plain pixel (or unitless) length. Percentages and
    /// font/viewport-relative units need a containing block or a font, neither of
    /// which is known at cascade time, and so are rejected.</summary>
    private static bool TryParsePixelLength(string value, out double pixels)
    {
        pixels = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2].Trim();

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out pixels)
            && double.IsFinite(pixels);
    }

    private static string FormatPixels(double pixels) =>
        pixels.ToString("0.####", CultureInfo.InvariantCulture) + "px";

    /// <summary>Formats a width÷height ratio as an <c>aspect-ratio</c> value.
    /// Written without spaces around the solidus: the layout parser splits on
    /// whitespace first, so a bare <c>/</c> token would be rejected.</summary>
    private static string FormatRatio(double ratio) =>
        ratio.ToString("R", CultureInfo.InvariantCulture) + "/1";

    /// <summary>
    /// Normalises an HTML dimension attribute value (width/height) to a CSS
    /// length.  Pure numeric values (e.g. "100") get a "px" suffix; values
    /// that already carry a unit or percentage (e.g. "100%", "10em") are
    /// returned unchanged after trimming.
    /// </summary>
    private static string NormaliseDimensionAttribute(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return "0px";

        // If the value already ends with a known unit or %, keep it as-is.
        if (trimmed[^1] == '%'
            || trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("em", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("pt", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("vw", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("vh", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        // Otherwise treat as a unitless pixel value.
        return trimmed + "px";
    }
}
