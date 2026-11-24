// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Runtime.CompilerServices;

namespace ASPIRE.Common.Memory;

/// <summary>
///     CuTe-inspired 2D Layout &amp; Stride Algebra descriptor (<c>Layout&lt;Shape, Stride&gt;</c>).
///     Encapsulates 2D coordinate-to-index bijections, tile slicing, transposition, ring-buffer row wrapping,
///     and XOR shared-memory bank-conflict-free swizzling without ad-hoc <c>y * width + x</c> pointer arithmetic.
/// </summary>
public readonly record struct TensorLayout2D(
    int Width,
    int Height,
    int StrideX,
    int StrideY,
    int BaseOffset = 0)
{
    /// <summary>
    ///     Gets the logical element count (<c>size(Layout) = Width * Height</c>).
    /// </summary>
    public int Size => Width <= 0 || Height <= 0 ? 0 : Width * Height;

    /// <summary>
    ///     Gets the codomain span extent (<c>cosize(Layout)</c>) required to index all valid coordinates
    ///     in <c>[0..Width-1, 0..Height-1]</c> starting from index 0.
    /// </summary>
    public int Cosize
    {
        get
        {
            if (Width <= 0 || Height <= 0)
            {
                return 0;
            }

            int maxXOffset = (Width - 1) * StrideX;
            int maxYOffset = (Height - 1) * StrideY;
            return BaseOffset + maxXOffset + maxYOffset + 1;
        }
    }

    /// <summary>
    ///     Creates a standard row-major 2D layout where consecutive X elements are contiguous (<c>StrideX = 1</c>).
    /// </summary>
    public static TensorLayout2D CreateRowMajor(int width, int height, int rowPitch = 0, int baseOffset = 0)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                "Width must be non-negative. Verify dimensions or run dotnet test --filter CutlassWave3OptimizationTests.");
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height,
                "Height must be non-negative. Verify dimensions or run dotnet test --filter CutlassWave3OptimizationTests.");
        }

        int resolvedPitch = rowPitch > 0 ? rowPitch : width;
        if (resolvedPitch < width)
        {
            throw new ArgumentOutOfRangeException(nameof(rowPitch), rowPitch,
                $"Row pitch ({resolvedPitch}) cannot be smaller than width ({width}).");
        }

        return new TensorLayout2D(width, height, 1, resolvedPitch, baseOffset);
    }

    /// <summary>
    ///     Creates a column-major 2D layout where consecutive Y elements are contiguous (<c>StrideY = 1</c>).
    /// </summary>
    public static TensorLayout2D CreateColumnMajor(int width, int height, int colPitch = 0, int baseOffset = 0)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                "Width must be non-negative. Verify dimensions or run dotnet test --filter CutlassWave3OptimizationTests.");
        }

        if (height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height,
                "Height must be non-negative. Verify dimensions or run dotnet test --filter CutlassWave3OptimizationTests.");
        }

        int resolvedPitch = colPitch > 0 ? colPitch : height;
        if (resolvedPitch < height)
        {
            throw new ArgumentOutOfRangeException(nameof(colPitch), colPitch,
                $"Column pitch ({resolvedPitch}) cannot be smaller than height ({height}).");
        }

        return new TensorLayout2D(width, height, resolvedPitch, 1, baseOffset);
    }

    /// <summary>
    ///     Maps logical 2D coordinates <c>(x, y)</c> to a 1D linear offset with bounds checking.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int LogicalToLinear(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x,
                $"Coordinate x={x} is outside layout width [0..{Width - 1}].");
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y,
                $"Coordinate y={y} is outside layout height [0..{Height - 1}].");
        }

        return BaseOffset + (x * StrideX) + (y * StrideY);
    }

    /// <summary>
    ///     Alias for <see cref="LogicalToLinear" /> matching CuTe <c>layout(x, y)</c> indexing semantics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOffset(int x, int y)
    {
        return LogicalToLinear(x, y);
    }

    /// <summary>
    ///     Maps logical 2D coordinates <c>(x, y)</c> to a 1D linear offset without bounds checks for hot inner loops.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOffsetUnchecked(int x, int y)
    {
        return BaseOffset + (x * StrideX) + (y * StrideY);
    }

    /// <summary>
    ///     Maps logical 2D coordinates <c>(x, y)</c> into a cyclic <paramref name="ringRows" /> row ring buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RingRowOffset(int x, int y, int ringRows)
    {
        if (ringRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ringRows), ringRows, "Ring row count must be positive.");
        }

        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x,
                $"Coordinate x={x} is outside ring layout width [0..{Width - 1}].");
        }

        if (y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, "Row coordinate y must be non-negative.");
        }

        int ringRow = y % ringRows;
        return BaseOffset + (x * StrideX) + (ringRow * StrideY);
    }

    /// <summary>
    ///     Slices a sub-tile window <c>[offsetX..offsetX+tileWidth, offsetY..offsetY+tileHeight]</c> preserving parent strides.
    /// </summary>
    public TensorLayout2D SliceTile(int offsetX, int offsetY, int tileWidth, int tileHeight)
    {
        if (offsetX < 0 || tileWidth < 0 || offsetX + tileWidth > Width)
        {
            throw new ArgumentOutOfRangeException(nameof(tileWidth),
                $"Sub-tile X range [{offsetX}..{offsetX + tileWidth}] exceeds parent width {Width}.");
        }

        if (offsetY < 0 || tileHeight < 0 || offsetY + tileHeight > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(tileHeight),
                $"Sub-tile Y range [{offsetY}..{offsetY + tileHeight}] exceeds parent height {Height}.");
        }

        int newBase = BaseOffset + (offsetX * StrideX) + (offsetY * StrideY);
        return new TensorLayout2D(tileWidth, tileHeight, StrideX, StrideY, newBase);
    }

    /// <summary>
    ///     Transposes the logical axes and strides (<c>(Width, Height, StrideX, StrideY) -&gt; (Height, Width, StrideY, StrideX)</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TensorLayout2D Transpose()
    {
        return new TensorLayout2D(Height, Width, StrideY, StrideX, BaseOffset);
    }

    /// <summary>
    ///     Composes this outer layout with a nested inner sub-layout (<c>composition(A, B)</c>).
    /// </summary>
    public TensorLayout2D Compose(TensorLayout2D innerLayout)
    {
        int composedBase = LogicalToLinear(innerLayout.BaseOffset % Math.Max(1, Width), innerLayout.BaseOffset / Math.Max(1, Width));
        int composedStrideX = innerLayout.StrideX * StrideX;
        int composedStrideY = innerLayout.StrideY * StrideY;
        return new TensorLayout2D(innerLayout.Width, innerLayout.Height, composedStrideX, composedStrideY, composedBase);
    }

    /// <summary>
    ///     Applies CuTe <c>Swizzle&lt;B, M, S&gt;</c> bit-field XOR permutation to a linear shared-memory offset:
    ///     <c>offset ^ ((offset &gt;&gt; swizzleShift) &amp; (((1 &lt;&lt; swizzleBits) - 1) &lt;&lt; swizzleBase))</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SwizzleXor(int offset, int swizzleBits, int swizzleBase, int swizzleShift)
    {
        if (swizzleBits <= 0)
        {
            return offset;
        }

        int bitMask = ((1 << swizzleBits) - 1) << swizzleBase;
        int shifted = swizzleShift >= 0 ? (offset >> swizzleShift) : (offset << (-swizzleShift));
        return offset ^ (shifted & bitMask);
    }

    /// <summary>
    ///     Computes the number of shared-memory bank collisions across columns <c>x in [0..min(Width, numBanks)-1]</c>
    ///     for a given <paramref name="row" /> under CuTe <c>Swizzle&lt;B, M, S&gt;</c>.
    /// </summary>
    public int CountRowBankConflicts(
        int row,
        int swizzleBits,
        int swizzleBase,
        int swizzleShift,
        int numBanks = 32)
    {
        if (numBanks <= 0 || numBanks > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(numBanks), numBanks, "Bank count must be in [1..64].");
        }

        if ((uint)row >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, $"Row {row} is outside [0..{Height - 1}].");
        }

        ulong occupiedBanksMask = 0UL;
        int conflicts = 0;
        int limitX = Math.Min(Width, numBanks);

        for (int x = 0; x < limitX; x++)
        {
            int rawOffset = GetOffsetUnchecked(x, row);
            int swizzledOffset = SwizzleXor(rawOffset, swizzleBits, swizzleBase, swizzleShift);
            int bank = ((swizzledOffset % numBanks) + numBanks) % numBanks;
            ulong bankBit = 1UL << bank;

            if ((occupiedBanksMask & bankBit) != 0UL)
            {
                conflicts++;
            }
            else
            {
                occupiedBanksMask |= bankBit;
            }
        }

        return conflicts;
    }

    /// <summary>
    ///     Returns <c>true</c> if all columns in <paramref name="row" /> (up to <paramref name="numBanks" />)
    ///     map to distinct shared-memory banks with zero bank conflicts.
    /// </summary>
    public bool IsBankConflictFreeForRow(
        int row,
        int swizzleBits,
        int swizzleBase,
        int swizzleShift,
        int numBanks = 32)
    {
        return CountRowBankConflicts(row, swizzleBits, swizzleBase, swizzleShift, numBanks) == 0;
    }
}
