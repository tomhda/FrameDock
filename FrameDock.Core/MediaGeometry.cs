using System.Globalization;

namespace FrameDock.Core;

/// <summary>Display-space and crop helpers shared with the editor UI.</summary>
public static class MediaGeometry
{
    /// <summary>
    /// Normalizes any whole-number angle to clockwise right-angle rotation.
    /// Non-right-angle metadata is rejected because the crop editor represents
    /// axis-aligned integer rectangles.
    /// </summary>
    public static int NormalizeRightAngleRotation(int degreesClockwise)
    {
        var normalized = ((degreesClockwise % 360) + 360) % 360;
        if (normalized % 90 != 0)
        {
            throw new ExportValidationException(Messages.Get("Error_RotationNotRightAngle"));
        }

        return normalized;
    }

    /// <summary>
    /// Returns the dimensions after SAR correction and rotation. SAR-corrected
    /// width is rounded to the nearest integer, with midpoint values rounded up.
    /// </summary>
    public static (int Width, int Height) ComputeDisplayDimensions(
        int codedWidth,
        int codedHeight,
        AspectRatio sampleAspectRatio,
        int rotationDegreesClockwise)
    {
        if (codedWidth <= 0 || codedHeight <= 0)
        {
            throw new ExportValidationException(Messages.Get("Error_VideoSizeUnknown"));
        }

        if (sampleAspectRatio.Numerator <= 0 || sampleAspectRatio.Denominator <= 0)
        {
            sampleAspectRatio = AspectRatio.Square;
        }

        var rotation = NormalizeRightAngleRotation(rotationDegreesClockwise);
        var correctedWidth = Math.Round(
            codedWidth * sampleAspectRatio.Value,
            MidpointRounding.AwayFromZero);
        if (!double.IsFinite(correctedWidth) || correctedWidth < 1 || correctedWidth > int.MaxValue)
        {
            throw new ExportValidationException(Messages.Get("Error_DisplaySizeInvalid"));
        }

        var width = checked((int)correctedWidth);
        return rotation is 90 or 270
            ? (codedHeight, width)
            : (width, codedHeight);
    }

    /// <summary>
    /// Returns the crop viewport's dimensions before the user's additional
    /// rotation. Zoom changes which pixels fill this viewport, not its canvas.
    /// </summary>
    public static (int Width, int Height) ComputeBaseViewportDimensions(MediaInfo media, CropRect? crop = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.DisplayWidth <= 0 || media.DisplayHeight <= 0)
        {
            throw new ExportValidationException(Messages.Get("Error_DisplaySizeWrong"));
        }

        if (crop is { } rectangle)
        {
            if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0 ||
                (long)rectangle.X + rectangle.Width > media.DisplayWidth ||
                (long)rectangle.Y + rectangle.Height > media.DisplayHeight)
            {
                throw new ExportValidationException(Messages.Get("Error_CropOutOfBounds"));
            }

            return (rectangle.Width, rectangle.Height);
        }

        return (media.DisplayWidth, media.DisplayHeight);
    }

    /// <summary>
    /// Computes the even-aligned inner source rectangle for a zoom in the
    /// oriented, square-pixel display space. Focus is normalized within the
    /// viewport (0 is the left/top edge, 1 is the right/bottom edge); the
    /// rectangle is clamped so it remains entirely inside that viewport.
    /// </summary>
    public static CropRect ComputeZoomSampleRect(CropRect viewport, double zoom, double focusX, double focusY)
    {
        ValidateZoom(zoom, focusX, focusY);
        if (viewport.X < 0 || viewport.Y < 0 || viewport.Width <= 0 || viewport.Height <= 0 ||
            ((viewport.X | viewport.Y) & 1) != 0)
        {
            throw new ExportValidationException(Messages.Get("Error_ZoomRectInvalid"));
        }

        if (zoom == 1.0)
        {
            return viewport;
        }

        var sampleWidth = FloorToEven(viewport.Width / zoom);
        var sampleHeight = FloorToEven(viewport.Height / zoom);
        if (sampleWidth < 2 || sampleHeight < 2)
        {
            throw new ExportValidationException(Messages.Get("Error_ZoomTooSmall"));
        }

        var maximumXOffset = ((viewport.Width - sampleWidth) / 2) * 2;
        var maximumYOffset = ((viewport.Height - sampleHeight) / 2) * 2;
        var xOffset = AlignFocusOffset(viewport.Width * focusX - sampleWidth / 2d, maximumXOffset);
        var yOffset = AlignFocusOffset(viewport.Height * focusY - sampleHeight / 2d, maximumYOffset);
        return new CropRect(
            checked(viewport.X + xOffset),
            checked(viewport.Y + yOffset),
            sampleWidth,
            sampleHeight);
    }

    /// <summary>
    /// Maps an oriented square-pixel display-space rectangle back to a coded
    /// source rectangle, undoing source metadata rotation and SAR scaling.
    /// Coded pixel boundaries are rounded outward so the returned rectangle
    /// covers the requested display region for GPU-side preview cropping.
    /// </summary>
    public static CropRect MapDisplayRectToCodedRect(MediaInfo media, CropRect displayRect)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.CodedWidth <= 0 || media.CodedHeight <= 0 || media.DisplayWidth <= 0 || media.DisplayHeight <= 0)
        {
            throw new ExportValidationException(Messages.Get("Error_DisplaySizeWrong"));
        }

        if (displayRect.X < 0 || displayRect.Y < 0 || displayRect.Width <= 0 || displayRect.Height <= 0 ||
            (long)displayRect.X + displayRect.Width > media.DisplayWidth ||
            (long)displayRect.Y + displayRect.Height > media.DisplayHeight)
        {
            throw new ExportValidationException(Messages.Get("Error_DisplayRectOutOfBounds"));
        }

        var rotation = NormalizeRightAngleRotation(media.RotationDegreesClockwise);
        var (preRotationWidth, preRotationHeight) = ComputeDisplayDimensions(
            media.CodedWidth,
            media.CodedHeight,
            media.SampleAspectRatio,
            0);
        var expectedDisplayDimensions = ComputeDisplayDimensions(
            media.CodedWidth,
            media.CodedHeight,
            media.SampleAspectRatio,
            rotation);
        if (expectedDisplayDimensions != (media.DisplayWidth, media.DisplayHeight))
        {
            throw new ExportValidationException(Messages.Get("Error_RotationSizeMismatch"));
        }

        var (preX, preY, preWidth, preHeight) = rotation switch
        {
            90 => (displayRect.Y, preRotationHeight - displayRect.X - displayRect.Width,
                displayRect.Height, displayRect.Width),
            180 => (preRotationWidth - displayRect.X - displayRect.Width,
                preRotationHeight - displayRect.Y - displayRect.Height,
                displayRect.Width, displayRect.Height),
            270 => (preRotationWidth - displayRect.Y - displayRect.Height, displayRect.X,
                displayRect.Height, displayRect.Width),
            _ => (displayRect.X, displayRect.Y, displayRect.Width, displayRect.Height)
        };

        if (preX < 0 || preY < 0 || preWidth <= 0 || preHeight <= 0 ||
            (long)preX + preWidth > preRotationWidth || (long)preY + preHeight > preRotationHeight)
        {
            throw new ExportValidationException(Messages.Get("Error_DisplayRectUnmappable"));
        }

        var left = ScaleFloor(preX, media.CodedWidth, preRotationWidth);
        var top = ScaleFloor(preY, media.CodedHeight, preRotationHeight);
        var right = ScaleCeiling(preX + preWidth, media.CodedWidth, preRotationWidth);
        var bottom = ScaleCeiling(preY + preHeight, media.CodedHeight, preRotationHeight);
        left = Math.Clamp(left, 0, media.CodedWidth - 1);
        top = Math.Clamp(top, 0, media.CodedHeight - 1);
        right = Math.Clamp(right, left + 1, media.CodedWidth);
        bottom = Math.Clamp(bottom, top + 1, media.CodedHeight);
        return new CropRect(left, top, right - left, bottom - top);
    }

    /// <summary>Validates the editor's export zoom and normalized focus point.</summary>
    public static void ValidateZoom(double zoom, double focusX, double focusY)
    {
        if (!double.IsFinite(zoom) || zoom is < 1.0 or > 4.0)
        {
            throw new ExportValidationException(Messages.Get("Error_ZoomRange"));
        }

        if (!double.IsFinite(focusX) || !double.IsFinite(focusY) || focusX is < 0 or > 1 || focusY is < 0 or > 1)
        {
            throw new ExportValidationException(Messages.Get("Error_ZoomFocusRange"));
        }
    }

    /// <summary>
    /// Validates that an explicit crop is inside the oriented display image and
    /// aligned to the 4:2:0 chroma grid used by the H.264 MP4 output.
    /// </summary>
    public static void ValidateCrop(CropRect crop, int displayWidth, int displayHeight)
    {
        if (displayWidth <= 0 || displayHeight <= 0)
        {
            throw new ExportValidationException(Messages.Get("Error_DisplaySizeWrong"));
        }

        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0)
        {
            throw new ExportValidationException(Messages.Get("Error_CropRectInvalid"));
        }

        if ((long)crop.X + crop.Width > displayWidth || (long)crop.Y + crop.Height > displayHeight)
        {
            throw new ExportValidationException(Messages.Get("Error_CropOutOfBounds"));
        }

        if (((crop.X | crop.Y | crop.Width | crop.Height) & 1) != 0)
        {
            throw new ExportValidationException(Messages.Get("Error_CropEvenRequired"));
        }
    }

    /// <summary>
    /// Builds the accurate-export video filter in this order: source SAR and
    /// metadata rotation, a crop in the oriented display space, then the user's
    /// additional clockwise rotation. The resulting pixels are square-pixel
    /// and carry no residual display matrix.
    /// </summary>
    internal static string BuildVideoFilter(
        MediaInfo media,
        CropRect? crop,
        int additionalRotationDegreesClockwise = 0,
        double playbackSpeed = 1.0,
        double zoomFactor = 1.0,
        double zoomFocusX = 0.5,
        double zoomFocusY = 0.5)
    {
        ValidateAdditionalRotation(additionalRotationDegreesClockwise);
        ValidatePlaybackSpeed(playbackSpeed);
        ValidateZoom(zoomFactor, zoomFocusX, zoomFocusY);

        var filters = new List<string>
        {
            $"scale={media.DisplayWidthBeforeRotation()}:{media.DisplayHeightBeforeRotation()}:flags=lanczos",
            "setsar=1"
        };

        switch (media.RotationDegreesClockwise)
        {
            case 90:
                filters.Add("transpose=clock");
                break;
            case 180:
                filters.Add("hflip,vflip");
                break;
            case 270:
                filters.Add("transpose=cclock");
                break;
        }

        if (crop is { } rectangle)
        {
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"crop={rectangle.Width}:{rectangle.Height}:{rectangle.X}:{rectangle.Y}"));
        }

        if (zoomFactor > 1.0)
        {
            var viewport = crop ?? new CropRect(0, 0, media.DisplayWidth, media.DisplayHeight);
            var sample = ComputeZoomSampleRect(viewport, zoomFactor, zoomFocusX, zoomFocusY);
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"crop={sample.Width}:{sample.Height}:{sample.X - viewport.X}:{sample.Y - viewport.Y}"));
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"scale={viewport.Width}:{viewport.Height}:flags=lanczos"));
        }

        switch (additionalRotationDegreesClockwise)
        {
            case 90:
                filters.Add("transpose=clock");
                break;
            case 180:
                filters.Add("hflip,vflip");
                break;
            case 270:
                filters.Add("transpose=cclock");
                break;
        }

        if (playbackSpeed != 1.0)
        {
            filters.Add($"setpts=PTS/{FormatFilterNumber(playbackSpeed)}");
        }

        // yuv420p requires even output dimensions. Padding the far edge keeps
        // uncropped, odd-sized input visible without changing crop coordinates.
        filters.Add("pad=ceil(iw/2)*2:ceil(ih/2)*2");
        filters.Add("setsar=1");
        // Display Matrix is per-frame side data and is not removed by metadata
        // mapping flags. The pixels are already oriented above; retaining it
        // would rotate the exported MP4 a second time in players.
        filters.Add("sidedata=mode=delete:type=DISPLAYMATRIX");
        return string.Join(',', filters);
    }

    internal static (int Width, int Height) ComputeExportDimensions(MediaInfo media, CropRect? crop, int additionalRotationDegreesClockwise)
    {
        ValidateAdditionalRotation(additionalRotationDegreesClockwise);
        var width = crop?.Width ?? media.DisplayWidth;
        var height = crop?.Height ?? media.DisplayHeight;
        if (additionalRotationDegreesClockwise is 90 or 270)
        {
            (width, height) = (height, width);
        }

        return (RoundUpToEven(width), RoundUpToEven(height));
    }

    internal static void ValidateAdditionalRotation(int degreesClockwise)
    {
        if (degreesClockwise is not (0 or 90 or 180 or 270))
        {
            throw new ExportValidationException(Messages.Get("Error_AdditionalRotationRange"));
        }
    }

    internal static void ValidatePlaybackSpeed(double playbackSpeed)
    {
        if (!double.IsFinite(playbackSpeed) || playbackSpeed is < 0.25 or > 4.0)
        {
            throw new ExportValidationException(Messages.Get("Error_PlaybackSpeedRange"));
        }
    }

    private static string FormatFilterNumber(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static int FloorToEven(double value)
    {
        if (!double.IsFinite(value) || value < 2 || value > int.MaxValue)
        {
            return 0;
        }

        return ((int)Math.Floor(value)) & ~1;
    }

    private static int AlignFocusOffset(double desiredOffset, int maximumOffset)
    {
        var nearestEven = (int)(Math.Floor(desiredOffset / 2d + 0.5d) * 2d);
        return Math.Clamp(nearestEven, 0, maximumOffset);
    }

    private static int ScaleFloor(int coordinate, int codedSize, int displaySize)
    {
        return checked((int)((long)coordinate * codedSize / displaySize));
    }

    private static int ScaleCeiling(int coordinate, int codedSize, int displaySize)
    {
        var numerator = (long)coordinate * codedSize;
        return checked((int)((numerator + displaySize - 1L) / displaySize));
    }

    internal static int RoundUpToEven(int value) => (value & 1) == 0 ? value : checked(value + 1);
}

internal static class MediaInfoGeometryExtensions
{
    public static int DisplayWidthBeforeRotation(this MediaInfo media)
    {
        return media.RotationDegreesClockwise is 90 or 270 ? media.DisplayHeight : media.DisplayWidth;
    }

    public static int DisplayHeightBeforeRotation(this MediaInfo media)
    {
        return media.RotationDegreesClockwise is 90 or 270 ? media.DisplayWidth : media.DisplayHeight;
    }
}
