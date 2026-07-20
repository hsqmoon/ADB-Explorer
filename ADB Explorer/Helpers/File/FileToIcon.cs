// Part of FileToIcon from Code Project article by Leung Yat Chun
// https://www.codeproject.com/Articles/32059/WPF-Filename-To-Icon-Converter
// Used and modified under the LGPLv3 license

using ADB_Explorer.Models;
using ADB_Explorer.Services;
using System.Drawing;
using static Services.NativeMethods;

namespace ADB_Explorer.Helpers;

public class FileToIconConverter
{
    public enum IconSize : uint
    {
        Large,
        Small,
        ExtraLarge,
        Jumbo,
        Thumbnail,
    }

    private const int FOLDER_ICON_INDEX = 3;
    private const int LINK_OVERLAY_INDEX = 29;
    private const int UNKNOWN_ICON_INDEX = 175;
    private const int BROKEN_LINK_ICON_INDEX = 271;

    private static readonly SysImageList _imgList = new(SysImageListSize.SHIL_JUMBO);

    // <summary>
    /// Return large file icon of the specified file.
    /// </summary>
    private static Icon GetFileIcon(string fileName, IconSize size)
    {
        var flags = NativeMethods.FileInfoFlags.SHGFI_SYSICONINDEX;
        
        if (!fileName.Contains(':'))
            flags = flags | NativeMethods.FileInfoFlags.SHGFI_USEFILEATTRIBUTES;

        if (size == IconSize.Small)
            flags = flags | NativeMethods.FileInfoFlags.SHGFI_SMALLICON;
        
        return NativeMethods.GetIcon(fileName, flags);
    }
    private static Icon GetIconFromIndex(int index, IconSize size)
        => NativeMethods.ExtractIconByIndex("Shell32.dll", index, size);

    private static Bitmap ResizeImage(Bitmap imgToResize, System.Drawing.Size size, int spacing, bool addBorder = true)
    {
        int destWidth = imgToResize.Width;
        int destHeight = imgToResize.Height;

        int leftOffset = (size.Width - destWidth) / 2;
        int topOffset = (size.Height - destHeight) / 2;

        Bitmap b = new Bitmap(size.Width, size.Height);
        using Graphics g = Graphics.FromImage(b);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.High;

        var Gray222 = System.Drawing.Color.FromArgb(222, 222, 222);
        var Gray225 = System.Drawing.Color.FromArgb(225, 225, 225);
        var Gray232 = System.Drawing.Color.FromArgb(232, 232, 232);
        var Gray244 = System.Drawing.Color.FromArgb(244, 244, 244);

        if (addBorder)
        {
            using var pen232 = new System.Drawing.Pen(Gray232);
            using var pen222 = new System.Drawing.Pen(Gray222);
            g.DrawRectangle(pen232,
                spacing + 1,
                spacing + 1,
                size.Width - (spacing + 1) * 2 - 1,
                size.Height - (spacing + 1) * 2 - 1);

            g.DrawRectangle(pen222,
                spacing,
                spacing,
                size.Width - spacing * 2 - 1,
                size.Height - spacing * 2 - 1);
        }

        g.DrawImage(imgToResize, leftOffset, topOffset, destWidth, destHeight);

        if (addBorder)
        {
            b.SetPixel(spacing, spacing, Gray244);
            b.SetPixel(spacing, size.Height - 1, Gray244);
            b.SetPixel(size.Width - 1, spacing, Gray244);
            b.SetPixel(size.Width - 1, size.Height - 1, Gray244);

            b.SetPixel(spacing + 1, spacing + 1, Gray225);
            b.SetPixel(spacing + 1, size.Height - 2, Gray225);
            b.SetPixel(size.Width - 2, spacing + 1, Gray225);
            b.SetPixel(size.Width - 2, size.Height - 2, Gray225);
        } 

        return b;
    }

    private static BitmapSource LoadBitmap(Bitmap source)
    {
        var hBitmap = source.GetHbitmap();
        //Memory Leak fixes, for more info : http://social.msdn.microsoft.com/forums/en-US/wpf/thread/edcf2482-b931-4939-9415-15b3515ddac6/
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty,
               BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.MDeleteObject(hBitmap);
        }
    }

    private static Bitmap LoadJumbo(int index, int desiredSize)
    {
        // Used to contain code to support OSs before Windows Vista
        // ADB Explorer requires at least Windows 10 build 18362

        _imgList.ImageListSize = SysImageListSize.SHIL_JUMBO;
        using Icon icon = _imgList.Icon(index);
        using Bitmap bitmap = icon.ToBitmap();

        var usable = FindUsableSize(bitmap);
        if (usable is SysImageListSize.SHIL_JUMBO)
        {
            // we are unable to downscale here, so it will be handled in the UI
            return ResizeImage(bitmap, new System.Drawing.Size(256, 256), 0, false);
        }

        _imgList.ImageListSize = usable;
        using Icon resizedIcon = _imgList.Icon(index);
        using Bitmap resizedBitmap = resizedIcon.ToBitmap();
        return ResizeImage(resizedBitmap, new System.Drawing.Size(desiredSize, desiredSize), 0);
    }

    private static Bitmap LoadJumbo(string lookup, int desiredSize)
    {
        _imgList.ImageListSize = SysImageListSize.SHIL_JUMBO;
        return LoadJumbo(_imgList.IconIndex(lookup), desiredSize);
    }

    private static SysImageListSize FindUsableSize(Bitmap bitmap)
    {
        System.Drawing.Color empty = System.Drawing.Color.FromArgb(0, 0, 0, 0);
        int ValidColumns = 0;

        for (int i = 0; i < bitmap.Width; i++)
        {
            int validPixels = 0;
            for (int j = 0; j < bitmap.Height; j++)
            {
                if (bitmap.GetPixel(i, j) != empty)
                    validPixels++;
            }
            if (validPixels > 0)
                ValidColumns++;
        }

        return ValidColumns switch
        {
            > 48 => SysImageListSize.SHIL_JUMBO,
            > 32 => SysImageListSize.SHIL_EXTRALARGE,
            > 16 => SysImageListSize.SHIL_LARGE,
            _ => SysImageListSize.SHIL_SMALL,
        };
    }

    private static BitmapSource CreateImage(
        string fileName,
        IconSize size,
        int desiredSize,
        AbstractFile.SpecialFileType specialType = AbstractFile.SpecialFileType.Regular) =>
        GetImage(fileName, size, desiredSize, specialType);

    private static BitmapSource CreateImage(Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        var source = LoadBitmap(bitmap);
        source.Freeze();
        return source;
    }

    private static BitmapSource GetImage(string fileName, IconSize size, int desiredSize, AbstractFile.SpecialFileType specialType = AbstractFile.SpecialFileType.Regular)
    {
        using var bitmap = GetBitmap(fileName, size, desiredSize, specialType);
        var source = LoadBitmap(bitmap);
        source.Freeze();
        return source;
    }

    private static Bitmap GetBitmap(string fileName, IconSize size, int desiredSize, AbstractFile.SpecialFileType specialType = AbstractFile.SpecialFileType.Regular)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        var lookup = specialType.HasFlag(AbstractFile.SpecialFileType.Regular)
            && extension.StartsWith('.')
                ? $"aaa{extension}"
                : fileName;

        var specialIndex = SpecialTypeIndex(specialType);

        switch (size)
        {
            case IconSize.Jumbo or IconSize.Thumbnail:
                return specialIndex < 0
                    ? LoadJumbo(lookup, desiredSize)
                    : LoadJumbo(specialIndex, desiredSize);

            case IconSize.ExtraLarge:
            {
                _imgList.ImageListSize = SysImageListSize.SHIL_EXTRALARGE;
                using Icon icon = _imgList.Icon(specialIndex < 0 ? _imgList.IconIndex(lookup) : specialIndex);
                return icon.ToBitmap();
            }

            default:
            {
                using Icon icon = specialIndex < 0 ? GetFileIcon(lookup, size) : GetIconFromIndex(specialIndex, size);
                return icon.ToBitmap();
            }
        }
    }

    private static int SpecialTypeIndex(AbstractFile.SpecialFileType specialType)
        => specialType switch
        {
            AbstractFile.SpecialFileType.Folder => FOLDER_ICON_INDEX,
            AbstractFile.SpecialFileType.BrokenLink => BROKEN_LINK_ICON_INDEX,
            AbstractFile.SpecialFileType.Unknown => UNKNOWN_ICON_INDEX,
            AbstractFile.SpecialFileType.LinkOverlay => LINK_OVERLAY_INDEX,
            _ => -1,
        };

    private static System.Drawing.Size IconToSize(IconSize size) => size switch
    {
        IconSize.Small => new(16, 16),
        IconSize.Large => new(32, 32),
        IconSize.ExtraLarge => new(48, 48),
        IconSize.Jumbo or IconSize.Thumbnail => new(256, 256),
        _ => throw new NotSupportedException(),
    };

    public static BitmapSource GetImage(string fileName, int iconSize, AbstractFile.SpecialFileType specialType = AbstractFile.SpecialFileType.Regular)
    {
        IconSize size = iconSize switch
        {
            <= 16 => IconSize.Small,
            <= 32 => IconSize.Large,
            <= 48 => IconSize.ExtraLarge,
            _ => IconSize.Jumbo,
        };

        return CreateImage(fileName, size, iconSize, specialType);
    }

    public static IEnumerable<BitmapSource> GetImage(string fileName, AbstractFile.SpecialFileType specialType, bool smallIcon = true)
    {
        var size = smallIcon ? IconSize.Small : IconSize.Jumbo;

        if (specialType is 0)
            yield break;
        
        if (specialType.HasFlag(AbstractFile.SpecialFileType.Apk))
        {
            using Icon apkIcon = new(Properties.AppGlobal.APK_icon, IconToSize(size));

            yield return CreateImage(apkIcon);
        }
        else
        {
            // Get icon without link overlay
            yield return CreateImage(fileName, size, smallIcon ? 16 : 96, specialType & ~AbstractFile.SpecialFileType.LinkOverlay);
        }

        if (specialType.HasFlag(AbstractFile.SpecialFileType.LinkOverlay))
        {
            // Get link overlay if required
            yield return CreateImage(fileName, size, smallIcon ? 16 : 96, AbstractFile.SpecialFileType.LinkOverlay);
        }
    }

}
