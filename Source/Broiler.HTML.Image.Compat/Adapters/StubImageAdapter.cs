using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Broiler.Graphics;
using Broiler.HTML.Adapters;
using Broiler.CSS;
using Broiler.Media.Image;

namespace Broiler.HTML.Image.Adapters;

/// <summary>
/// Resource factory for the compatibility backend. Retains the OS-free managed
/// logic (CSS color parsing, SVG intrinsic sizing) and routes pens/brushes/fonts
/// and image decoding through the stub compat backend; raster output still comes
/// from the managed Broiler pipeline.
/// </summary>
internal sealed class StubImageAdapter : RAdapter
{
    private const int MinSvgRasterLongestSide = 128;
    private const int MaxSvgRasterScale = 8;

    private readonly IFontTypefaceResolver _typefaceResolver;
    private readonly IPaintCompatFactory _paintCompatFactory;

    internal StubImageAdapter(
        IFontTypefaceResolver typefaceResolver = null,
        IReadOnlyCollection<string> systemFonts = null,
        IPaintCompatFactory paintCompatFactory = null)
    {
        _typefaceResolver = typefaceResolver ?? CompatProvider.CreateFontTypefaceResolver();
        _paintCompatFactory = paintCompatFactory ?? CompatProvider.PaintCompatFactory;

        // Register system fonts first so we can probe availability below.
        var distinctSystemFonts = new HashSet<string>(
            systemFonts ?? BroilerFontRegistry.GetSystemFontFamilies(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var familyName in distinctSystemFonts)
        {
            AddFontFamily(new FontFamilyAdapter(familyName));
        }

        foreach (var mapping in FontFamilyFallbackPolicy.ResolveDefaultMappings(distinctSystemFonts))
        {
            AddFontFamilyMapping(mapping.Key, mapping.Value);
        }
    }

    public static StubImageAdapter Instance { get; } = new();

    internal bool HasDeferredLoadedTypefacePath(string family) =>
        _typefaceResolver.HasDeferredLoadedTypefacePath(family);

    internal bool HasMaterializedLoadedTypeface(string family) =>
        _typefaceResolver.HasMaterializedLoadedTypeface(family);

    /// <summary>
    /// Loads a TrueType/OpenType font from a file path and registers it as
    /// an available font family.  Optionally maps a CSS name to the loaded
    /// family (e.g. mapping "sans-serif" to a bundled reference font for
    /// deterministic test comparison).
    /// </summary>
    /// <param name="path">Absolute path to a .ttf or .otf font file.</param>
    /// <param name="mapFromName">
    /// When non-null, registers the font under this CSS family alias instead
    /// of eagerly probing the file for its embedded family name.
    /// </param>
    /// <returns>
    /// The registered family name (the alias when provided), or <c>null</c> on failure.
    /// </returns>
    public override string LoadFontFromFile(string path, string mapFromName = null)
    {
        var familyName = _typefaceResolver.RegisterFontFile(path, mapFromName);
        if (string.IsNullOrWhiteSpace(familyName))
            return null;

        BroilerFontRegistry.RegisterFontFile(path, familyName);
        AddFontFamily(new FontFamilyAdapter(familyName));
        return familyName;
    }

    protected override BColor GetColorInt(string colorName)
    {
        if (TryParseHexColor(colorName, out var color))
            return color;

        if (CssSystemColors.TryResolve(colorName, out CssColor sys))
            return BColor.FromArgb(sys.Alpha, sys.Red, sys.Green, sys.Blue);

        // Fallback: try common color names (CSS 2.1 basic + CSS Color Level 3 extended)
        return colorName.ToLowerInvariant() switch
        {
            // CSS 2.1 §4.3.6 basic color keywords
            "white" => BColor.FromArgb(255, 255, 255, 255),
            "black" => BColor.FromArgb(255, 0, 0, 0),
            "red" => BColor.FromArgb(255, 255, 0, 0),
            "green" => BColor.FromArgb(255, 0, 128, 0),
            "blue" => BColor.FromArgb(255, 0, 0, 255),
            "yellow" => BColor.FromArgb(255, 255, 255, 0),
            "orange" => BColor.FromArgb(255, 255, 165, 0),
            "purple" => BColor.FromArgb(255, 128, 0, 128),
            "gray" or "grey" => BColor.FromArgb(255, 128, 128, 128),
            "silver" => BColor.FromArgb(255, 192, 192, 192),
            "maroon" => BColor.FromArgb(255, 128, 0, 0),
            "olive" => BColor.FromArgb(255, 128, 128, 0),
            "lime" => BColor.FromArgb(255, 0, 255, 0),
            "aqua" or "cyan" => BColor.FromArgb(255, 0, 255, 255),
            "teal" => BColor.FromArgb(255, 0, 128, 128),
            "navy" => BColor.FromArgb(255, 0, 0, 128),
            "fuchsia" or "magenta" => BColor.FromArgb(255, 255, 0, 255),
            "transparent" => BColor.FromArgb(0, 255, 255, 255),

            // CSS Color Level 3 extended color keywords (used by WPT tests)
            "lightgray" or "lightgrey" => BColor.FromArgb(255, 211, 211, 211),
            "darkgray" or "darkgrey" => BColor.FromArgb(255, 169, 169, 169),
            "dimgray" or "dimgrey" => BColor.FromArgb(255, 105, 105, 105),
            "lightslategray" or "lightslategrey" => BColor.FromArgb(255, 119, 136, 153),
            "slategray" or "slategrey" => BColor.FromArgb(255, 112, 128, 144),
            "darkslategray" or "darkslategrey" => BColor.FromArgb(255, 47, 79, 79),
            "gainsboro" => BColor.FromArgb(255, 220, 220, 220),
            "whitesmoke" => BColor.FromArgb(255, 245, 245, 245),
            "aliceblue" => BColor.FromArgb(255, 240, 248, 255),
            "ghostwhite" => BColor.FromArgb(255, 248, 248, 255),
            "snow" => BColor.FromArgb(255, 255, 250, 250),
            "seashell" => BColor.FromArgb(255, 255, 245, 238),
            "floralwhite" => BColor.FromArgb(255, 255, 250, 240),
            "linen" => BColor.FromArgb(255, 250, 240, 230),
            "antiquewhite" => BColor.FromArgb(255, 250, 235, 215),
            "oldlace" => BColor.FromArgb(255, 253, 245, 230),
            "papayawhip" => BColor.FromArgb(255, 255, 239, 213),
            "blanchedalmond" => BColor.FromArgb(255, 255, 235, 205),
            "bisque" => BColor.FromArgb(255, 255, 228, 196),
            "peachpuff" => BColor.FromArgb(255, 255, 218, 185),
            "navajowhite" => BColor.FromArgb(255, 255, 222, 173),
            "moccasin" => BColor.FromArgb(255, 255, 228, 181),
            "cornsilk" => BColor.FromArgb(255, 255, 248, 220),
            "ivory" => BColor.FromArgb(255, 255, 255, 240),
            "lemonchiffon" => BColor.FromArgb(255, 255, 250, 205),
            "lightyellow" => BColor.FromArgb(255, 255, 255, 224),
            "lightgoldenrodyellow" => BColor.FromArgb(255, 250, 250, 210),
            "beige" => BColor.FromArgb(255, 245, 245, 220),
            "wheat" => BColor.FromArgb(255, 245, 222, 179),
            "sandybrown" => BColor.FromArgb(255, 244, 164, 96),
            "goldenrod" => BColor.FromArgb(255, 218, 165, 32),
            "darkgoldenrod" => BColor.FromArgb(255, 184, 134, 11),
            "gold" => BColor.FromArgb(255, 255, 215, 0),
            "khaki" => BColor.FromArgb(255, 240, 230, 140),
            "darkkhaki" => BColor.FromArgb(255, 189, 183, 107),
            "tan" => BColor.FromArgb(255, 210, 180, 140),
            "burlywood" => BColor.FromArgb(255, 222, 184, 135),
            "peru" => BColor.FromArgb(255, 205, 133, 63),
            "chocolate" => BColor.FromArgb(255, 210, 105, 30),
            "sienna" => BColor.FromArgb(255, 160, 82, 45),
            "saddlebrown" => BColor.FromArgb(255, 139, 69, 19),
            "brown" => BColor.FromArgb(255, 165, 42, 42),
            "firebrick" => BColor.FromArgb(255, 178, 34, 34),
            "darkred" => BColor.FromArgb(255, 139, 0, 0),
            "indianred" => BColor.FromArgb(255, 205, 92, 92),
            "rosybrown" => BColor.FromArgb(255, 188, 143, 143),
            "lightcoral" => BColor.FromArgb(255, 240, 128, 128),
            "salmon" => BColor.FromArgb(255, 250, 128, 114),
            "darksalmon" => BColor.FromArgb(255, 233, 150, 122),
            "lightsalmon" => BColor.FromArgb(255, 255, 160, 122),
            "coral" => BColor.FromArgb(255, 255, 127, 80),
            "tomato" => BColor.FromArgb(255, 255, 99, 71),
            "orangered" => BColor.FromArgb(255, 255, 69, 0),
            "darkorange" => BColor.FromArgb(255, 255, 140, 0),
            "crimson" => BColor.FromArgb(255, 220, 20, 60),
            "deeppink" => BColor.FromArgb(255, 255, 20, 147),
            "hotpink" => BColor.FromArgb(255, 255, 105, 180),
            "lightpink" => BColor.FromArgb(255, 255, 182, 193),
            "pink" => BColor.FromArgb(255, 255, 192, 203),
            "palevioletred" => BColor.FromArgb(255, 219, 112, 147),
            "mediumvioletred" => BColor.FromArgb(255, 199, 21, 133),
            "orchid" => BColor.FromArgb(255, 218, 112, 214),
            "plum" => BColor.FromArgb(255, 221, 160, 221),
            "violet" => BColor.FromArgb(255, 238, 130, 238),
            "mediumpurple" => BColor.FromArgb(255, 147, 112, 219),
            "darkorchid" => BColor.FromArgb(255, 153, 50, 204),
            "darkviolet" => BColor.FromArgb(255, 148, 0, 211),
            "darkmagenta" => BColor.FromArgb(255, 139, 0, 139),
            "blueviolet" => BColor.FromArgb(255, 138, 43, 226),
            "indigo" => BColor.FromArgb(255, 75, 0, 130),
            "rebeccapurple" => BColor.FromArgb(255, 102, 51, 153),
            "slateblue" => BColor.FromArgb(255, 106, 90, 205),
            "darkslateblue" => BColor.FromArgb(255, 72, 61, 139),
            "mediumslateblue" => BColor.FromArgb(255, 123, 104, 238),
            "lavender" => BColor.FromArgb(255, 230, 230, 250),
            "thistle" => BColor.FromArgb(255, 216, 191, 216),
            "mistyrose" => BColor.FromArgb(255, 255, 228, 225),
            "lavenderblush" => BColor.FromArgb(255, 255, 240, 245),
            "honeydew" => BColor.FromArgb(255, 240, 255, 240),
            "mintcream" => BColor.FromArgb(255, 245, 255, 250),
            "azure" => BColor.FromArgb(255, 240, 255, 255),
            "lightsteelblue" => BColor.FromArgb(255, 176, 196, 222),
            "powderblue" => BColor.FromArgb(255, 176, 224, 230),
            "lightblue" => BColor.FromArgb(255, 173, 216, 230),
            "skyblue" => BColor.FromArgb(255, 135, 206, 235),
            "lightskyblue" => BColor.FromArgb(255, 135, 206, 250),
            "deepskyblue" => BColor.FromArgb(255, 0, 191, 255),
            "dodgerblue" => BColor.FromArgb(255, 30, 144, 255),
            "cornflowerblue" => BColor.FromArgb(255, 100, 149, 237),
            "steelblue" => BColor.FromArgb(255, 70, 130, 180),
            "royalblue" => BColor.FromArgb(255, 65, 105, 225),
            "mediumblue" => BColor.FromArgb(255, 0, 0, 205),
            "darkblue" => BColor.FromArgb(255, 0, 0, 139),
            "midnightblue" => BColor.FromArgb(255, 25, 25, 112),
            "cadetblue" => BColor.FromArgb(255, 95, 158, 160),
            "paleturquoise" => BColor.FromArgb(255, 175, 238, 238),
            "turquoise" => BColor.FromArgb(255, 64, 224, 208),
            "mediumturquoise" => BColor.FromArgb(255, 72, 209, 204),
            "darkturquoise" => BColor.FromArgb(255, 0, 206, 209),
            "lightcyan" => BColor.FromArgb(255, 224, 255, 255),
            "mediumaquamarine" => BColor.FromArgb(255, 102, 205, 170),
            "aquamarine" => BColor.FromArgb(255, 127, 255, 212),
            "darkseagreen" => BColor.FromArgb(255, 143, 188, 143),
            "mediumseagreen" => BColor.FromArgb(255, 60, 179, 113),
            "seagreen" => BColor.FromArgb(255, 46, 139, 87),
            "darkcyan" => BColor.FromArgb(255, 0, 139, 139),
            "lightseagreen" => BColor.FromArgb(255, 32, 178, 170),
            "lightgreen" => BColor.FromArgb(255, 144, 238, 144),
            "palegreen" => BColor.FromArgb(255, 152, 251, 152),
            "springgreen" => BColor.FromArgb(255, 0, 255, 127),
            "mediumspringgreen" => BColor.FromArgb(255, 0, 250, 154),
            "lawngreen" => BColor.FromArgb(255, 124, 252, 0),
            "chartreuse" => BColor.FromArgb(255, 127, 255, 0),
            "greenyellow" => BColor.FromArgb(255, 173, 255, 47),
            "yellowgreen" => BColor.FromArgb(255, 154, 205, 50),
            "limegreen" => BColor.FromArgb(255, 50, 205, 50),
            "forestgreen" => BColor.FromArgb(255, 34, 139, 34),
            "darkgreen" => BColor.FromArgb(255, 0, 100, 0),
            "olivedrab" => BColor.FromArgb(255, 107, 142, 35),
            "darkolivegreen" => BColor.FromArgb(255, 85, 107, 47),

            _ => ResolveExtendedColorName(colorName),
        };
    }

    /// <summary>
    /// Fallback for CSS named colors not in the primary switch. Uses
    /// <see cref="BColor.FromName"/> which recognises the CSS/X11 named colors
    /// (case-insensitive). Returns black if the name is unrecognised.
    /// </summary>
    private static BColor ResolveExtendedColorName(string colorName)
    {
        var c = BColor.FromName(colorName);
        if (!c.IsEmpty)
            return BColor.FromArgb(c.A, c.R, c.G, c.B);
        return BColor.FromArgb(255, 0, 0, 0);
    }

    private static bool TryParseHexColor(string colorName, out BColor color)
    {
        color = BColor.Empty;
        if (string.IsNullOrWhiteSpace(colorName) || colorName[0] != '#')
            return false;

        return colorName.Length switch
        {
            4 => TryParseHexColor(colorName, 1, 1, hasAlpha: false, out color),
            5 => TryParseHexColor(colorName, 1, 1, hasAlpha: true, out color),
            7 => TryParseHexColor(colorName, 1, 2, hasAlpha: false, out color),
            9 => TryParseHexColor(colorName, 1, 2, hasAlpha: true, out color),
            _ => false,
        };
    }

    private static bool TryParseHexColor(string colorName, int start, int digitsPerChannel, bool hasAlpha, out BColor color)
    {
        color = BColor.Empty;
        if (!TryParseHexChannel(colorName, start, digitsPerChannel, out var r)
            || !TryParseHexChannel(colorName, start + digitsPerChannel, digitsPerChannel, out var g)
            || !TryParseHexChannel(colorName, start + (digitsPerChannel * 2), digitsPerChannel, out var b))
        {
            return false;
        }

        var alpha = 255;
        if (hasAlpha
            && !TryParseHexChannel(colorName, start + (digitsPerChannel * 3), digitsPerChannel, out alpha))
        {
            return false;
        }

        color = BColor.FromArgb(alpha, r, g, b);
        return true;
    }

    private static bool TryParseHexChannel(string colorName, int start, int digits, out int value)
    {
        value = 0;
        for (int i = 0; i < digits; i++)
        {
            var digit = ConvertHexDigit(colorName[start + i]);
            if (digit < 0)
                return false;

            value = (value << 4) + digit;
        }

        if (digits == 1)
            value = (value << 4) + value;

        return true;
    }

    private static int ConvertHexDigit(char c)
    {
        if (c is >= '0' and <= '9')
            return c - '0';
        if (c is >= 'a' and <= 'f')
            return c - 'a' + 10;
        if (c is >= 'A' and <= 'F')
            return c - 'A' + 10;

        return -1;
    }

    protected override RPen CreatePen(BColor color)
    {
        return new PenAdapter(
            (strokeWidth, dashStyle) => _paintCompatFactory.CreatePenPaint(color, strokeWidth, dashStyle),
            _paintCompatFactory.UpdatePenPaint)
        {
            SolidColor = new BColor(color.R, color.G, color.B, color.A),
        };
    }

    protected override RBrush CreateSolidBrush(BColor color)
    {
        return new BrushAdapter(
            () => _paintCompatFactory.CreateSolidBrushPaint(color),
            dispose: true)
        {
            SolidColor = new BColor(color.R, color.G, color.B, color.A),
        };
    }

    protected override RBrush CreateLinearGradientBrush(RectangleF rect, BColor color1, BColor color2, double angle)
    {
        return new BrushAdapter(
            () => _paintCompatFactory.CreateLinearGradientBrushPaint(rect, color1, color2, angle),
            dispose: true);
    }

    protected override RImage ConvertImageInt(object image) =>
        // There is no native bitmap type to convert without an OS graphics backend.
        // Callers should decode encoded image bytes through ImageFromStream instead.
        throw new NotSupportedException(
            "Converting a platform bitmap is not supported without an OS graphics backend; use ImageFromStream with encoded image data.");

    protected override RImage ImageFromStreamInt(Stream memoryStream)
    {
        // Read the stream into a byte array so we can inspect the content
        // before attempting a bitmap decode and can still route SVG input
        // through the dedicated Broiler rasterizer.
        byte[] data;
        if (memoryStream is MemoryStream ms)
        {
            data = ms.ToArray();
        }
        else if (memoryStream.CanSeek)
        {
            data = new byte[memoryStream.Length - memoryStream.Position];
            _ = memoryStream.Read(data, 0, data.Length);
        }
        else
        {
            using var copy = new MemoryStream();
            memoryStream.CopyTo(copy);
            data = copy.ToArray();
        }

        if (BSvgRasterizer.IsSvgData(data))
        {
            return RasterizeSvg(data);
        }

        try
        {
            // An animated image shows the frame its own timeline has reached at the pass's
            // presentation time. This is the only seam where the frame is chosen — everything
            // above holds one decoded bitmap — so the clock is read here rather than threaded
            // down through the container and the load handlers.
            return new ImageAdapter(BBitmap.DecodeFrameAt(data, ImageAnimationClock.PresentationTime));
        }
        catch (ArgumentException)
        {
            // Unrecognised or corrupt image data.
            return null;
        }
        catch (NotSupportedException)
        {
            // Encoded image loading is handled by Broiler.Media-backed image paths.
            return null;
        }
    }

    /// <summary>
    /// Rasterizes SVG data to a backend-neutral bitmap through <see cref="BSvgRasterizer"/>.
    /// Parses width/height from the SVG root element to determine output
    /// dimensions.  Per the HTML spec, when the SVG does not specify both
    /// explicit width AND height the intrinsic size is 300×150 (the default
    /// replaced element size).  This matches browser behaviour (Chromium).
    /// </summary>
    private static RImage RasterizeSvg(byte[] data)
    {
        var svgContent = System.Text.Encoding.UTF8.GetString(data);

        // Parse the SVG root element's width, height and viewBox attributes.
        var (svgWidth, svgHeight, vbRatio, hasDegenerateViewBox) = ParseSvgIntrinsicDimensions(svgContent);
        bool preserveAspectRatioNone = HasPreserveAspectRatioNone(svgContent);

        bool parsedIntrinsicWidth = svgWidth > 0;
        bool parsedIntrinsicHeight = svgHeight > 0;
        int width, height;
        // The size to rasterize at, which is a different question from what the image reports
        // as its intrinsic size: with both width and height present the SVG is rasterized at
        // them, and otherwise at the 300×150 default object size, which is a canvas to draw
        // on rather than a claim about the image. What it reports is decided at the bottom of
        // this method, from width/height/viewBox per SVG 2 §8.2.
        bool hasBothDimensions = parsedIntrinsicWidth && parsedIntrinsicHeight;
        if (hasBothDimensions)
        {
            width = (int)Math.Ceiling(svgWidth);
            height = (int)Math.Ceiling(svgHeight);
        }
        else
        {
            width = 300;
            height = 150;
        }

        // A rasterization choice only: an SVG that gives one dimension and stretches to the
        // other has no viewport of its own to draw into, so it is rendered oversized and
        // cropped to its content instead. It used to double as the reported-intrinsics gate,
        // which hid a width the SVG states outright — `width="8px"` with a viewBox sized to
        // 24×384 in a 128×384 box rather than the 8×128 the ratio asks for.
        bool renderOversizedAndCrop =
            preserveAspectRatioNone && vbRatio > 0 && !hasBothDimensions;

        int rasterScale = GetSvgRasterizationScale(width, height, hasBothDimensions, vbRatio);
        int rasterWidth = width * rasterScale;
        int rasterHeight = height * rasterScale;

        BBitmap? bitmap;
        if (hasDegenerateViewBox)
        {
            bitmap = new BBitmap(rasterWidth, rasterHeight);
            bitmap.Erase(BColor.Transparent);
        }
        else if (renderOversizedAndCrop)
        {
            const int fallbackRenderScale = 4;
            int renderWidth = rasterWidth * fallbackRenderScale;
            int renderHeight = rasterHeight * fallbackRenderScale;
            bool shouldInjectViewportForMissingWidth = !parsedIntrinsicWidth && parsedIntrinsicHeight;
            if (shouldInjectViewportForMissingWidth)
            {
                var svgForRender = EnsureSvgViewport(svgContent, svgWidth, svgHeight, rasterWidth, rasterHeight);
                bitmap = RenderSvgToBitmap(svgForRender, renderWidth, renderHeight);
            }
            else
            {
                bitmap = RenderSvgToBitmap(svgContent, renderWidth, renderHeight);
            }
            if (bitmap != null && !IsBitmapFullyTransparent(bitmap))
            {
                bitmap = NormalizeSvgContentBounds(bitmap, renderWidth, renderHeight);
            }
            else
            {
                bitmap?.Dispose();
                var svgForRender = EnsureSvgViewport(svgContent, svgWidth, svgHeight, rasterWidth, rasterHeight);
                bitmap = RenderSvgToBitmap(svgForRender, renderWidth, renderHeight);
            }
        }
        else
        {
            // SVGs that use percentage-based dimensions internally (e.g.
            // width="100%" on child elements) need explicit viewport dimensions
            // on the root <svg> element for percentage resolution.  Inject the
            // computed width/height before parsing when the root element is
            // missing one or both attributes.
            var svgForRender = EnsureSvgViewport(svgContent, svgWidth, svgHeight, rasterWidth, rasterHeight);
            bitmap = RenderSvgToBitmap(svgForRender, rasterWidth, rasterHeight);
            if (bitmap != null
                && rasterScale > 1
                && preserveAspectRatioNone
                && vbRatio > 0)
            {
                bitmap = NormalizeSvgContentBounds(bitmap, rasterWidth, rasterHeight);
            }
        }

        if (bitmap == null)
            return null;

        if (!hasDegenerateViewBox && TryParseSolidViewportFill(svgContent, out var solidFill))
            bitmap.Erase(solidFill);

        // Only SVGs with both explicit width and height have intrinsic dimensions. A viewBox
        // gives an intrinsic ratio whatever preserveAspectRatio says (SVG 2 §8.2 decides the
        // intrinsic sizing properties from width/height/viewBox alone): preserveAspectRatio
        // governs how the content is fitted once the viewport size is known, which is a later
        // question than what size the image asks to be. Reading `none` as "no intrinsic ratio"
        // made every such image size to the whole background positioning area, so the entire
        // css-backgrounds/background-size/vector family — 60 reftests whose SVGs all carry
        // preserveAspectRatio="none" — painted a stretched image where the ratio was the thing
        // under test.
        bool hasIntrinsicRatio = hasBothDimensions || vbRatio > 0;
        double intrinsicRatio = hasBothDimensions ? svgWidth / svgHeight : vbRatio;
        return new ImageAdapter(bitmap,
            hasIntrinsicRatio: hasIntrinsicRatio,
            hasIntrinsicWidth: parsedIntrinsicWidth,
            hasIntrinsicHeight: parsedIntrinsicHeight,
            intrinsicAspectRatio: intrinsicRatio > 0 ? intrinsicRatio : null,
            intrinsicWidth: parsedIntrinsicWidth ? svgWidth : 0,
            intrinsicHeight: parsedIntrinsicHeight ? svgHeight : 0);
    }

    private static int GetSvgRasterizationScale(int width, int height, bool hasBothDimensions, double viewBoxRatio)
    {
        if (!hasBothDimensions || viewBoxRatio <= 0)
            return 1;

        int longestSide = Math.Max(width, height);
        if (longestSide >= MinSvgRasterLongestSide)
            return 1;

        return Math.Clamp((int)Math.Ceiling((double)MinSvgRasterLongestSide / longestSide), 1, MaxSvgRasterScale);
    }

    private static BBitmap? RenderSvgToBitmap(string svgContent, int width, int height)
        => BSvgRasterizer.RasterizeToBitmap(svgContent, width, height);

    private static BBitmap NormalizeSvgContentBounds(BBitmap bitmap, int width, int height)
    {
        var rowHasOpaque = new bool[bitmap.Height];
        var colHasOpaque = new bool[bitmap.Width];
        int minX = bitmap.Width;
        int minY = bitmap.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A == 0)
                    continue;

                rowHasOpaque[y] = true;
                colHasOpaque[x] = true;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY
            || (minX == 0 && minY == 0 && maxX == bitmap.Width - 1 && maxY == bitmap.Height - 1))
            return bitmap;

        int croppedWidth = maxX - minX + 1;
        int croppedHeight = maxY - minY + 1;
        var nonEmptyCols = new List<int>(croppedWidth);
        for (int x = minX; x <= maxX; x++)
        {
            if (colHasOpaque[x])
                nonEmptyCols.Add(x);
        }

        var nonEmptyRows = new List<int>(croppedHeight);
        for (int y = minY; y <= maxY; y++)
        {
            if (rowHasOpaque[y])
                nonEmptyRows.Add(y);
        }

        if (nonEmptyCols.Count == 0 || nonEmptyRows.Count == 0)
            return bitmap;

        using var condensed = new BBitmap(nonEmptyCols.Count, nonEmptyRows.Count);
        for (int destY = 0; destY < nonEmptyRows.Count; destY++)
        {
            int srcY = nonEmptyRows[destY];
            for (int destX = 0; destX < nonEmptyCols.Count; destX++)
            {
                condensed.SetPixel(destX, destY, bitmap.GetPixel(nonEmptyCols[destX], srcY));
            }
        }

        var normalized = condensed.ResizeNearest(width, height);
        bitmap.Dispose();
        return normalized;
    }

    /// <summary>
    /// Ensures the SVG root element has explicit width and height attributes
    /// so that percentage-based child dimensions (e.g. width="100%") can
    /// resolve correctly.  Returns the original content unchanged when both
    /// attributes are already present.
    /// </summary>
    private static string EnsureSvgViewport(
        string svgContent,
        double parsedWidth, double parsedHeight,
        int targetWidth, int targetHeight)
    {
        // Both intrinsic dimensions are present – nothing to inject.
        if (parsedWidth > 0 && parsedHeight > 0)
            return svgContent;

        int svgIdx = svgContent.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (svgIdx < 0)
            return svgContent;

        int tagEnd = svgContent.IndexOf('>', svgIdx);
        if (tagEnd < 0)
            return svgContent;

        var tag = svgContent.Substring(svgIdx, tagEnd - svgIdx + 1);
        var updatedTag = tag;
        bool needsViewportDimensions = parsedWidth <= 0 || parsedHeight <= 0;
        if (needsViewportDimensions)
        {
            // When either intrinsic dimension is missing, SVG rasterization needs a
            // concrete viewport size for percentage children. Preserve any
            // parsed absolute dimension on the other axis, but always inject
            // an explicit width/height pair so partial-dimension SVGs have a
            // concrete viewport.
            int effectiveWidth = parsedWidth > 0
                ? (int)Math.Ceiling(parsedWidth)
                : targetWidth;
            updatedTag = System.Text.RegularExpressions.Regex.Replace(
                updatedTag,
                @"\swidth\s*=\s*[""'][^""']*[""']",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            updatedTag = updatedTag.Insert(updatedTag.Length - 1, $" width=\"{effectiveWidth}\"");
        }

        if (needsViewportDimensions)
        {
            int effectiveHeight = parsedHeight > 0
                ? (int)Math.Ceiling(parsedHeight)
                : targetHeight;
            updatedTag = System.Text.RegularExpressions.Regex.Replace(
                updatedTag,
                @"\sheight\s*=\s*[""'][^""']*[""']",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            updatedTag = updatedTag.Insert(updatedTag.Length - 1, $" height=\"{effectiveHeight}\"");
        }

        return svgContent.Substring(0, svgIdx) + updatedTag + svgContent[(tagEnd + 1)..];
    }

    /// <summary>
    /// Parses the SVG root element to extract intrinsic width, height and
    /// viewBox aspect ratio.  Returns (width, height, viewBoxRatio,
    /// hasDegenerateViewBox) where
    /// values ≤ 0 indicate the attribute was absent or non-numeric.
    /// </summary>
    private static (double width, double height, double viewBoxRatio, bool hasDegenerateViewBox) ParseSvgIntrinsicDimensions(string svg)
    {
        double w = -1, h = -1, ratio = -1;
        bool hasDegenerateViewBox = false;

        // Find the <svg ...> opening tag.
        int svgIdx = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (svgIdx < 0) return (w, h, ratio, hasDegenerateViewBox);
        int tagEnd = svg.IndexOf('>', svgIdx);
        if (tagEnd < 0) return (w, h, ratio, hasDegenerateViewBox);
        var tag = svg.Substring(svgIdx, tagEnd - svgIdx + 1);

        // Parse width attribute (only absolute units / plain numbers).
        w = ParseSvgLengthAttribute(tag, "width");
        h = ParseSvgLengthAttribute(tag, "height");

        // Parse viewBox for aspect ratio.
        var vbMatch = System.Text.RegularExpressions.Regex.Match(
            tag, @"viewBox\s*=\s*[""']([^""']+)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (vbMatch.Success)
        {
            var parts = vbMatch.Groups[1].Value.Split(
                [' ', ',', '\t', '\n', '\r'],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double vbW)
                && double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double vbH))
            {
                if (vbW > 0 && vbH > 0)
                    ratio = vbW / vbH;
                else
                    hasDegenerateViewBox = true;
            }
        }

        return (w, h, ratio, hasDegenerateViewBox);
    }

    private static bool HasPreserveAspectRatioNone(string svg)
    {
        int svgIdx = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (svgIdx < 0) return false;
        int tagEnd = svg.IndexOf('>', svgIdx);
        if (tagEnd < 0) return false;
        var tag = svg.Substring(svgIdx, tagEnd - svgIdx + 1);

        var m = System.Text.RegularExpressions.Regex.Match(
            tag,
            @"(?<!\w)preserveAspectRatio\s*=\s*[""']([^""']+)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        return m.Groups[1].Value.Trim().StartsWith("none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts a numeric length value from an SVG attribute.  Returns the
    /// value in pixels for plain numbers and "px" units; returns -1 for
    /// percentage values or absent attributes.
    /// </summary>
    private static double ParseSvgLengthAttribute(string tag, string name)
    {
        // Match name="value" but avoid matching longer attribute names
        // (e.g. "width" should not match "stroke-width").
        var m = System.Text.RegularExpressions.Regex.Match(
            tag, @"(?<!\w)" + name + @"\s*=\s*[""']([^""']+)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return -1;

        var val = m.Groups[1].Value.Trim();
        // Ignore percentage values – they are not intrinsic dimensions.
        if (val.EndsWith('%')) return -1;
        // Strip "px" suffix.
        if (val.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            val = val[..^2];

        return double.TryParse(val, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0 ? v : -1;
    }

    private static bool IsBitmapFullyTransparent(BBitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A != 0)
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseSolidViewportFill(string svgContent, out BColor color)
    {
        color = BColor.Transparent;

        var rectMatches = System.Text.RegularExpressions.Regex.Matches(
            svgContent,
            @"<rect\b[^>]*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (rectMatches.Count != 1)
            return false;

        var rectTag = rectMatches[0].Value;
        bool fillsViewport =
            System.Text.RegularExpressions.Regex.IsMatch(rectTag, @"\bwidth\s*=\s*[""']100%[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && System.Text.RegularExpressions.Regex.IsMatch(rectTag, @"\bheight\s*=\s*[""']100%[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!fillsViewport)
            return false;

        var fillMatch = System.Text.RegularExpressions.Regex.Match(
            rectTag,
            @"\bfill\s*=\s*[""']([^""']+)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!fillMatch.Success)
            return false;

        var fillValue = fillMatch.Groups[1].Value.Trim();

        // Managed parse (hex or known CSS/.NET color name); avoids the OS-dependent
        // System.Drawing.ColorTranslator.
        if (TryParseHexColor(fillValue, out var hex))
        {
            color = new BColor(hex.R, hex.G, hex.B, hex.A);
            return true;
        }

        var named = BColor.FromName(fillValue);
        if (!named.IsEmpty)
        {
            color = new BColor(named.R, named.G, named.B, named.A);
            return true;
        }

        return false;
    }

    protected override RFont CreateFontInt(string family, double size, Graphics.FontStyle style)
    {
        return new FontAdapter(family, size, style, () => _typefaceResolver.ResolveTypeface(family, style));
    }

    protected override RFont CreateFontInt(RFontFamily family, double size, Graphics.FontStyle style) => CreateFontInt(family.Name, size, style);

    protected override object GetClipboardDataObjectInt(string html, string plainText) =>
        new ClipboardPayload(html, plainText);

    protected override void SetToClipboardInt(string text) =>
        LastClipboardPayload = new ClipboardPayload(null, text);

    protected override void SetToClipboardInt(string html, string plainText) =>
        LastClipboardPayload = new ClipboardPayload(html, plainText);

    protected override void SetToClipboardInt(RImage image) =>
        LastClipboardPayload = image;

    protected override RContextMenu CreateContextMenuInt() => new StubContextMenuAdapter();

    internal static object LastClipboardPayload { get; private set; }

    private sealed record ClipboardPayload(string Html, string PlainText);

    private sealed class StubContextMenuAdapter : RContextMenu
    {
        private int _itemsCount;

        public override int ItemsCount => _itemsCount;

        public override void AddDivider() => _itemsCount++;

        public override void AddItem(string text, bool enabled, EventHandler onClick) => _itemsCount++;

        public override void RemoveLastDivider()
        {
            if (_itemsCount > 0)
                _itemsCount--;
        }

        public override void Show(RControl parent, PointF location)
        {
            // Platform-neutral frontend: host applications can implement their own menu.
        }

        public override void Dispose()
        {
        }
    }
}
