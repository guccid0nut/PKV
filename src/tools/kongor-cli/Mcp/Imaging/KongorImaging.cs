// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Runtime.InteropServices;
using ImageMagick;

namespace Kongor.Cli.Mcp.Imaging;

/// <summary>
/// Unmanaged pixel contract for cleanroom TAO MCP pixel buffers backed by Magick.NET-Q16-AnyCPU (Apache-2.0, 0 CVEs).
/// </summary>
public interface IPixelValue<TSelf>
    where TSelf : unmanaged, IPixelValue<TSelf>
{
    Rgba32 ToRgba32();
    static abstract TSelf FromRgba32(Rgba32 rgba);
    static abstract PixelMapping? NativeMagickMapping { get; }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgba32 : IPixelValue<Rgba32>, IEquatable<Rgba32>
{
    public byte R;
    public byte G;
    public byte B;
    public byte A;

    public Rgba32(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public Rgba32(uint packedValue)
    {
        R = (byte)(packedValue & 0xFFu);
        G = (byte)((packedValue >> 8) & 0xFFu);
        B = (byte)((packedValue >> 16) & 0xFFu);
        A = (byte)((packedValue >> 24) & 0xFFu);
    }

    public readonly uint PackedValue =>
        (uint)R | ((uint)G << 8) | ((uint)B << 16) | ((uint)A << 24);

    public static PixelMapping? NativeMagickMapping => PixelMapping.RGBA;

    public readonly Rgba32 ToRgba32() => this;

    public static Rgba32 FromRgba32(Rgba32 rgba) => rgba;

    public readonly bool Equals(Rgba32 other) => PackedValue == other.PackedValue;

    public override readonly bool Equals(object? obj) => obj is Rgba32 other && Equals(other);

    public override readonly int GetHashCode() => (int)PackedValue;

    public static bool operator ==(Rgba32 left, Rgba32 right) => left.Equals(right);

    public static bool operator !=(Rgba32 left, Rgba32 right) => !left.Equals(right);

    public override readonly string ToString() => $"Rgba32({R}, {G}, {B}, {A})";
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Bgra32 : IPixelValue<Bgra32>, IEquatable<Bgra32>
{
    public byte B;
    public byte G;
    public byte R;
    public byte A;

    public Bgra32(byte b, byte g, byte r, byte a = 255)
    {
        B = b;
        G = g;
        R = r;
        A = a;
    }

    public static PixelMapping? NativeMagickMapping => PixelMapping.BGRA;

    public readonly Rgba32 ToRgba32() => new(R, G, B, A);

    public static Bgra32 FromRgba32(Rgba32 rgba) => new(rgba.B, rgba.G, rgba.R, rgba.A);

    public readonly bool Equals(Bgra32 other) =>
        B == other.B && G == other.G && R == other.R && A == other.A;

    public override readonly bool Equals(object? obj) => obj is Bgra32 other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(B, G, R, A);

    public static bool operator ==(Bgra32 left, Bgra32 right) => left.Equals(right);

    public static bool operator !=(Bgra32 left, Bgra32 right) => !left.Equals(right);

    public override readonly string ToString() => $"Bgra32({B}, {G}, {R}, {A})";
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct L8 : IPixelValue<L8>, IEquatable<L8>
{
    public byte PackedValue;

    public L8(byte luminance)
    {
        PackedValue = luminance;
    }

    public static PixelMapping? NativeMagickMapping => null;

    public readonly Rgba32 ToRgba32() => new(PackedValue, PackedValue, PackedValue, 255);

    public static L8 FromRgba32(Rgba32 rgba)
    {
        int lum = (int)Math.Round((0.299 * rgba.R) + (0.587 * rgba.G) + (0.114 * rgba.B));
        return new L8((byte)Math.Clamp(lum, 0, 255));
    }

    public readonly bool Equals(L8 other) => PackedValue == other.PackedValue;

    public override readonly bool Equals(object? obj) => obj is L8 other && Equals(other);

    public override readonly int GetHashCode() => PackedValue;

    public static bool operator ==(L8 left, L8 right) => left.Equals(right);

    public static bool operator !=(L8 left, L8 right) => !left.Equals(right);
}

public static class Color
{
    public static Rgba32 Black => new(0, 0, 0, 255);
    public static Rgba32 White => new(255, 255, 255, 255);
    public static Rgba32 Transparent => new(0, 0, 0, 0);
    public static Rgba32 Goldenrod => new(218, 165, 32, 255);
    public static Rgba32 Magenta => new(255, 0, 255, 255);
    public static Rgba32 Red => new(255, 0, 0, 255);
    public static Rgba32 Green => new(0, 128, 0, 255);
    public static Rgba32 Blue => new(0, 0, 255, 255);
    public static Rgba32 Yellow => new(255, 255, 0, 255);
}

public readonly record struct Point(int X, int Y);

public readonly record struct Size(int Width, int Height);

public readonly record struct Rectangle(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;

    public static Rectangle Intersect(Rectangle a, Rectangle b)
    {
        int x1 = Math.Max(a.X, b.X);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int y1 = Math.Max(a.Y, b.Y);
        int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (x2 >= x1 && y2 >= y1)
        {
            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }

        return new Rectangle(0, 0, 0, 0);
    }
}

public interface IResampler;

public static class KnownResamplers
{
    private sealed class BicubicResampler : IResampler;
    private sealed class NearestNeighborResampler : IResampler;

    public static IResampler Bicubic { get; } = new BicubicResampler();
    public static IResampler NearestNeighbor { get; } = new NearestNeighborResampler();
}

public enum ResizeMode
{
    Crop,
    Pad,
    BoxPad,
    Max,
    Min,
    Stretch
}

public sealed class ResizeOptions
{
    public Size Size { get; set; }
    public IResampler Sampler { get; set; } = KnownResamplers.Bicubic;
    public ResizeMode Mode { get; set; } = ResizeMode.Stretch;
}

public enum FlipMode
{
    None,
    Horizontal,
    Vertical
}

public interface IImageProcessingContext
{
    IImageProcessingContext Resize(int width, int height);
    IImageProcessingContext Resize(ResizeOptions options);
    IImageProcessingContext Crop(Rectangle cropRectangle);
    IImageProcessingContext Crop(int width, int height);
    IImageProcessingContext Flip(FlipMode flipMode);
    IImageProcessingContext BackgroundColor(Rgba32 color);
    IImageProcessingContext DrawImage(Image foreground, Point location, float opacity = 1.0f);
}

public sealed class PixelRowAccessor<TPixel>
    where TPixel : unmanaged
{
    private readonly TPixel[] _pixels;
    private readonly int _width;
    private readonly int _height;

    internal PixelRowAccessor(TPixel[] pixels, int width, int height)
    {
        _pixels = pixels;
        _width = width;
        _height = height;
    }

    public int Width => _width;
    public int Height => _height;

    public Span<TPixel> GetRowSpan(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)_height)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        }

        return _pixels.AsSpan(rowIndex * _width, _width);
    }
}

public abstract class Image : IDisposable
{
    private bool _disposed;

    public int Width { get; protected set; }
    public int Height { get; protected set; }

    public abstract Image Clone();
    public abstract Image Clone(Action<IImageProcessingContext> operation);
    public abstract void Mutate(Action<IImageProcessingContext> operation);
    public abstract void SaveAsPng(string path);
    public abstract void SaveAsPng(Stream stream);
    public abstract Task SaveAsPngAsync(string path, CancellationToken cancellationToken = default);
    public abstract Task SaveAsPngAsync(Stream stream, CancellationToken cancellationToken = default);

    internal abstract Rgba32 GetRgba32At(int x, int y);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    public static Image Load(string path) => Load<Rgba32>(path);

    public static Image Load(byte[] data) => Load<Rgba32>(data);

    public static Image Load(ReadOnlySpan<byte> data) => Load<Rgba32>(data);

    public static Image Load(Stream stream) => Load<Rgba32>(stream);

    public static Image<TPixel> Load<TPixel>(string path)
        where TPixel : unmanaged, IPixelValue<TPixel>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using MagickImage magick = new(path);
        return Image<TPixel>.FromMagickImage(magick);
    }

    public static Image<TPixel> Load<TPixel>(byte[] data)
        where TPixel : unmanaged, IPixelValue<TPixel>
    {
        ArgumentNullException.ThrowIfNull(data);
        using MagickImage magick = new(data);
        return Image<TPixel>.FromMagickImage(magick);
    }

    public static Image<TPixel> Load<TPixel>(ReadOnlySpan<byte> data)
        where TPixel : unmanaged, IPixelValue<TPixel>
    {
        using MagickImage magick = new(data);
        return Image<TPixel>.FromMagickImage(magick);
    }

    public static Image<TPixel> Load<TPixel>(Stream stream)
        where TPixel : unmanaged, IPixelValue<TPixel>
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MagickImage magick = new(stream);
        return Image<TPixel>.FromMagickImage(magick);
    }

    public static async Task<Image> LoadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        await LoadAsync<Rgba32>(stream, cancellationToken).ConfigureAwait(false);

    public static async Task<Image> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        await LoadAsync<Rgba32>(path, cancellationToken).ConfigureAwait(false);

    public static async Task<Image<TPixel>> LoadAsync<TPixel>(Stream stream, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelValue<TPixel>
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream ms = new();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return Load<TPixel>(ms.ToArray());
    }

    public static async Task<Image<TPixel>> LoadAsync<TPixel>(string path, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelValue<TPixel>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Load<TPixel>(bytes);
    }

    public static Image<TPixel> LoadPixelData<TPixel>(byte[] data, int width, int height)
        where TPixel : unmanaged, IPixelValue<TPixel> =>
        LoadPixelData<TPixel>(data.AsSpan(), width, height);

    public static unsafe Image<TPixel> LoadPixelData<TPixel>(ReadOnlySpan<byte> data, int width, int height)
        where TPixel : unmanaged, IPixelValue<TPixel>
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int requiredBytes = checked(width * height * sizeof(TPixel));
        if (data.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Pixel buffer length ({data.Length}) is smaller than required {requiredBytes} bytes for {width}x{height} {typeof(TPixel).Name}.",
                nameof(data));
        }

        Image<TPixel> image = new(width, height);
        ReadOnlySpan<TPixel> srcPixels = MemoryMarshal.Cast<byte, TPixel>(data.Slice(0, requiredBytes));
        srcPixels.CopyTo(image.GetPixelSpan());
        return image;
    }

    public static Image<TPixel> LoadPixelData<TPixel>(ReadOnlySpan<TPixel> data, int width, int height)
        where TPixel : unmanaged, IPixelValue<TPixel>
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int count = checked(width * height);
        if (data.Length < count)
        {
            throw new ArgumentException($"Pixel span length ({data.Length}) is smaller than {count}.", nameof(data));
        }

        Image<TPixel> image = new(width, height);
        data.Slice(0, count).CopyTo(image.GetPixelSpan());
        return image;
    }
}

public sealed class Image<TPixel> : Image
    where TPixel : unmanaged, IPixelValue<TPixel>
{
    private TPixel[] _pixels;
    private int _pixelCount;
    private bool _isPooled;

    public Image(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _pixelCount = checked(width * height);
        _pixels = ArrayPool<TPixel>.Shared.Rent(_pixelCount);
        _isPooled = true;
        _pixels.AsSpan(0, _pixelCount).Clear();
    }

    public Image(int width, int height, TPixel backgroundColor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _pixelCount = checked(width * height);
        _pixels = ArrayPool<TPixel>.Shared.Rent(_pixelCount);
        _isPooled = true;
        _pixels.AsSpan(0, _pixelCount).Fill(backgroundColor);
    }

    private Image(int width, int height, TPixel[] pixels, bool isPooled)
    {
        Width = width;
        Height = height;
        _pixelCount = checked(width * height);
        _pixels = pixels;
        _isPooled = isPooled;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _isPooled && _pixels.Length > 0)
        {
            TPixel[] toReturn = _pixels;
            _pixels = [];
            _pixelCount = 0;
            _isPooled = false;
            ArrayPool<TPixel>.Shared.Return(toReturn);
        }

        base.Dispose(disposing);
    }

    public TPixel this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                throw new ArgumentOutOfRangeException($"Pixel ({x}, {y}) is outside bounds {Width}x{Height}.");
            }

            return _pixels[(y * Width) + x];
        }
        set
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                throw new ArgumentOutOfRangeException($"Pixel ({x}, {y}) is outside bounds {Width}x{Height}.");
            }

            _pixels[(y * Width) + x] = value;
        }
    }

    internal Span<TPixel> GetPixelSpan() => _pixels.AsSpan(0, _pixelCount);

    internal override Rgba32 GetRgba32At(int x, int y) => _pixels[(y * Width) + x].ToRgba32();

    public void CopyPixelDataTo(Span<byte> destination)
    {
        ReadOnlySpan<byte> srcBytes = MemoryMarshal.AsBytes(GetPixelSpan());
        if (destination.Length < srcBytes.Length)
        {
            throw new ArgumentException($"Destination span ({destination.Length}) is smaller than {srcBytes.Length} bytes.", nameof(destination));
        }

        srcBytes.CopyTo(destination);
    }

    public void CopyPixelDataTo(Span<TPixel> destination)
    {
        Span<TPixel> srcSpan = GetPixelSpan();
        if (destination.Length < srcSpan.Length)
        {
            throw new ArgumentException($"Destination span ({destination.Length}) is smaller than {srcSpan.Length} pixels.", nameof(destination));
        }

        srcSpan.CopyTo(destination);
    }

    public void ProcessPixelRows(Action<PixelRowAccessor<TPixel>> processPixels)
    {
        ArgumentNullException.ThrowIfNull(processPixels);
        PixelRowAccessor<TPixel> accessor = new(_pixels, Width, Height);
        processPixels(accessor);
    }

    public void ProcessPixelRows<TPixel2>(
        Image<TPixel2> image2,
        Action<PixelRowAccessor<TPixel>, PixelRowAccessor<TPixel2>> processPixels)
        where TPixel2 : unmanaged, IPixelValue<TPixel2>
    {
        ArgumentNullException.ThrowIfNull(image2);
        ArgumentNullException.ThrowIfNull(processPixels);
        PixelRowAccessor<TPixel> acc1 = new(_pixels, Width, Height);
        PixelRowAccessor<TPixel2> acc2 = new(image2._pixels, image2.Width, image2.Height);
        processPixels(acc1, acc2);
    }

    public void ProcessPixelRows<TPixel2, TPixel3>(
        Image<TPixel2> image2,
        Image<TPixel3> image3,
        Action<PixelRowAccessor<TPixel>, PixelRowAccessor<TPixel2>, PixelRowAccessor<TPixel3>> processPixels)
        where TPixel2 : unmanaged, IPixelValue<TPixel2>
        where TPixel3 : unmanaged, IPixelValue<TPixel3>
    {
        ArgumentNullException.ThrowIfNull(image2);
        ArgumentNullException.ThrowIfNull(image3);
        ArgumentNullException.ThrowIfNull(processPixels);
        PixelRowAccessor<TPixel> acc1 = new(_pixels, Width, Height);
        PixelRowAccessor<TPixel2> acc2 = new(image2._pixels, image2.Width, image2.Height);
        PixelRowAccessor<TPixel3> acc3 = new(image3._pixels, image3.Width, image3.Height);
        processPixels(acc1, acc2, acc3);
    }

    public override Image<TPixel> Clone()
    {
        TPixel[] copy = ArrayPool<TPixel>.Shared.Rent(_pixelCount);
        GetPixelSpan().CopyTo(copy.AsSpan(0, _pixelCount));
        return new Image<TPixel>(Width, Height, copy, isPooled: true);
    }

    public override Image<TPixel> Clone(Action<IImageProcessingContext> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Image<TPixel> copy = Clone();
        copy.Mutate(operation);
        return copy;
    }

    public override void Mutate(Action<IImageProcessingContext> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ProcessingContext context = new(this);
        operation(context);
    }

    public override void SaveAsPng(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using MagickImage magick = ToMagickImage();
        magick.Write(path, MagickFormat.Png32);
    }

    public override void SaveAsPng(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MagickImage magick = ToMagickImage();
        magick.Write(stream, MagickFormat.Png32);
    }

    public override async Task SaveAsPngAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using MemoryStream ms = new();
        SaveAsPng(ms);
        await File.WriteAllBytesAsync(path, ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public override async Task SaveAsPngAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream ms = new();
        SaveAsPng(ms);
        ms.Position = 0;
        await ms.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static Image<TPixel> FromMagickImage(MagickImage magick)
    {
        int w = Math.Max(1, (int)magick.Width);
        int h = Math.Max(1, (int)magick.Height);
        PixelMapping? directMapping = TPixel.NativeMagickMapping;

        using IPixelCollection<ushort> pixelCollection = magick.GetPixelsUnsafe();
        if (directMapping.HasValue)
        {
            byte[] raw = pixelCollection.ToByteArray(directMapping.Value)
                ?? throw new InvalidOperationException("Magick.NET returned null pixel buffer.");
            return LoadPixelData<TPixel>(raw, w, h);
        }

        byte[] rgbaBytes = pixelCollection.ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidOperationException("Magick.NET returned null RGBA buffer.");
        ReadOnlySpan<Rgba32> rgbaSpan = MemoryMarshal.Cast<byte, Rgba32>(rgbaBytes.AsSpan());
        int count = checked(w * h);
        TPixel[] dst = ArrayPool<TPixel>.Shared.Rent(count);
        for (int i = 0; i < count; i++)
        {
            dst[i] = TPixel.FromRgba32(rgbaSpan[i]);
        }

        return new Image<TPixel>(w, h, dst, isPooled: true);
    }

    private MagickImage ToMagickImage()
    {
        PixelMapping? directMapping = TPixel.NativeMagickMapping;
        if (directMapping.HasValue)
        {
            byte[] rawBytes = MemoryMarshal.AsBytes(GetPixelSpan()).ToArray();
            PixelReadSettings settings = new((uint)Width, (uint)Height, StorageType.Char, directMapping.Value);
            return new MagickImage(rawBytes, settings);
        }

        byte[] rgba = new byte[_pixelCount * 4];
        Span<Rgba32> rgbaSpan = MemoryMarshal.Cast<byte, Rgba32>(rgba.AsSpan());
        for (int i = 0; i < _pixelCount; i++)
        {
            rgbaSpan[i] = _pixels[i].ToRgba32();
        }

        PixelReadSettings rgbaSettings = new((uint)Width, (uint)Height, StorageType.Char, PixelMapping.RGBA);
        return new MagickImage(rgba, rgbaSettings);
    }

    private void ReplaceBuffer(int newWidth, int newHeight, TPixel[] newPixels, bool isPooled)
    {
        if (_isPooled && _pixels.Length > 0 && !ReferenceEquals(_pixels, newPixels))
        {
            ArrayPool<TPixel>.Shared.Return(_pixels);
        }

        Width = newWidth;
        Height = newHeight;
        _pixelCount = checked(newWidth * newHeight);
        _pixels = newPixels;
        _isPooled = isPooled;
    }

    private sealed class ProcessingContext : IImageProcessingContext
    {
        private readonly Image<TPixel> _owner;

        public ProcessingContext(Image<TPixel> owner)
        {
            _owner = owner;
        }

        public IImageProcessingContext Resize(int width, int height)
        {
            int targetW = Math.Max(1, width);
            int targetH = Math.Max(1, height);
            if (targetW == _owner.Width && targetH == _owner.Height)
            {
                return this;
            }

            using MagickImage magick = _owner.ToMagickImage();
            magick.FilterType = FilterType.Catrom;
            MagickGeometry geometry = new((uint)targetW, (uint)targetH)
            {
                IgnoreAspectRatio = true
            };
            magick.Resize(geometry);
            Image<TPixel> resized = FromMagickImage(magick);
            TPixel[] stolen = resized._pixels;
            bool stolenPooled = resized._isPooled;
            resized._pixels = [];
            resized._pixelCount = 0;
            resized._isPooled = false;
            resized.Dispose();
            _owner.ReplaceBuffer(targetW, targetH, stolen, stolenPooled);
            return this;
        }

        public IImageProcessingContext Resize(ResizeOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Resize(options.Size.Width, options.Size.Height);
        }

        public IImageProcessingContext Crop(int width, int height) =>
            Crop(new Rectangle(0, 0, width, height));

        public IImageProcessingContext Crop(Rectangle cropRectangle)
        {
            Rectangle clamped = Rectangle.Intersect(cropRectangle, new Rectangle(0, 0, _owner.Width, _owner.Height));
            if (clamped.Width <= 0 || clamped.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cropRectangle), "Crop rectangle does not intersect image bounds.");
            }

            int newCount = checked(clamped.Width * clamped.Height);
            TPixel[] cropped = ArrayPool<TPixel>.Shared.Rent(newCount);
            for (int y = 0; y < clamped.Height; y++)
            {
                ReadOnlySpan<TPixel> srcRow = _owner._pixels.AsSpan(((clamped.Y + y) * _owner.Width) + clamped.X, clamped.Width);
                srcRow.CopyTo(cropped.AsSpan(y * clamped.Width, clamped.Width));
            }

            _owner.ReplaceBuffer(clamped.Width, clamped.Height, cropped, isPooled: true);
            return this;
        }

        public IImageProcessingContext Flip(FlipMode flipMode)
        {
            if (flipMode == FlipMode.None)
            {
                return this;
            }

            int w = _owner.Width;
            int h = _owner.Height;
            TPixel[] flipped = ArrayPool<TPixel>.Shared.Rent(checked(w * h));

            if (flipMode == FlipMode.Vertical)
            {
                for (int y = 0; y < h; y++)
                {
                    ReadOnlySpan<TPixel> srcRow = _owner._pixels.AsSpan(y * w, w);
                    srcRow.CopyTo(flipped.AsSpan((h - 1 - y) * w, w));
                }
            }
            else if (flipMode == FlipMode.Horizontal)
            {
                for (int y = 0; y < h; y++)
                {
                    int rowBase = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        flipped[rowBase + (w - 1 - x)] = _owner._pixels[rowBase + x];
                    }
                }
            }

            _owner.ReplaceBuffer(w, h, flipped, isPooled: true);
            return this;
        }

        public IImageProcessingContext BackgroundColor(Rgba32 color)
        {
            _owner.GetPixelSpan().Fill(TPixel.FromRgba32(color));
            return this;
        }

        public IImageProcessingContext DrawImage(Image foreground, Point location, float opacity = 1.0f)
        {
            ArgumentNullException.ThrowIfNull(foreground);
            float clampedOpacity = Math.Clamp(opacity, 0.0f, 1.0f);
            if (clampedOpacity <= 0.0f)
            {
                return this;
            }

            int dstW = _owner.Width;
            int dstH = _owner.Height;
            int srcW = foreground.Width;
            int srcH = foreground.Height;

            for (int sy = 0; sy < srcH; sy++)
            {
                int dy = location.Y + sy;
                if ((uint)dy >= (uint)dstH)
                {
                    continue;
                }

                for (int sx = 0; sx < srcW; sx++)
                {
                    int dx = location.X + sx;
                    if ((uint)dx >= (uint)dstW)
                    {
                        continue;
                    }

                    Rgba32 fg = foreground.GetRgba32At(sx, sy);
                    float srcA = (fg.A / 255.0f) * clampedOpacity;
                    if (srcA >= 0.999f)
                    {
                        _owner._pixels[(dy * dstW) + dx] = TPixel.FromRgba32(new Rgba32(fg.R, fg.G, fg.B, 255));
                        continue;
                    }

                    if (srcA <= 0.001f)
                    {
                        continue;
                    }

                    Rgba32 bg = _owner._pixels[(dy * dstW) + dx].ToRgba32();
                    float dstA = bg.A / 255.0f;
                    float outA = srcA + (dstA * (1.0f - srcA));
                    if (outA <= 1e-6f)
                    {
                        _owner._pixels[(dy * dstW) + dx] = TPixel.FromRgba32(new Rgba32(0, 0, 0, 0));
                        continue;
                    }

                    byte outR = (byte)Math.Clamp((int)Math.Round(((fg.R * srcA) + (bg.R * dstA * (1.0f - srcA))) / outA), 0, 255);
                    byte outG = (byte)Math.Clamp((int)Math.Round(((fg.G * srcA) + (bg.G * dstA * (1.0f - srcA))) / outA), 0, 255);
                    byte outB = (byte)Math.Clamp((int)Math.Round(((fg.B * srcA) + (bg.B * dstA * (1.0f - srcA))) / outA), 0, 255);
                    byte outAlphaByte = (byte)Math.Clamp((int)Math.Round(outA * 255.0f), 0, 255);
                    _owner._pixels[(dy * dstW) + dx] = TPixel.FromRgba32(new Rgba32(outR, outG, outB, outAlphaByte));
                }
            }

            return this;
        }
    }
}

public static class ImageExtensions
{
    public static void SaveAsPng(Image image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.SaveAsPng(path);
    }

    public static void SaveAsPng(Image image, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.SaveAsPng(stream);
    }

    public static Task SaveAsPngAsync(Image image, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.SaveAsPngAsync(path, cancellationToken);
    }

    public static Task SaveAsPngAsync(Image image, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.SaveAsPngAsync(stream, cancellationToken);
    }
}
