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
            throw new ExportValidationException("この動画の回転情報は 90 度単位ではないため、クロップ位置を計算できません。");
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
            throw new ExportValidationException("動画の映像サイズを読み取れませんでした。");
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
            throw new ExportValidationException("動画の表示サイズが大きすぎるか、正しくありません。");
        }

        var width = checked((int)correctedWidth);
        return rotation is 90 or 270
            ? (codedHeight, width)
            : (width, codedHeight);
    }

    /// <summary>
    /// Validates that an explicit crop is inside the oriented display image and
    /// aligned to the 4:2:0 chroma grid used by the H.264 MP4 output.
    /// </summary>
    public static void ValidateCrop(CropRect crop, int displayWidth, int displayHeight)
    {
        if (displayWidth <= 0 || displayHeight <= 0)
        {
            throw new ExportValidationException("動画の表示サイズが正しくありません。");
        }

        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0)
        {
            throw new ExportValidationException("クロップ範囲の位置とサイズを確認してください。");
        }

        if ((long)crop.X + crop.Width > displayWidth || (long)crop.Y + crop.Height > displayHeight)
        {
            throw new ExportValidationException("クロップ範囲が動画の表示領域を超えています。");
        }

        if (((crop.X | crop.Y | crop.Width | crop.Height) & 1) != 0)
        {
            throw new ExportValidationException("H.264 の出力では、クロップ位置とサイズを偶数ピクセルにしてください。");
        }
    }

    internal static string BuildVideoFilter(MediaInfo media, CropRect? crop)
    {
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
