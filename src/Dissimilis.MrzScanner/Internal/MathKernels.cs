using System.Numerics;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Dissimilis.MrzScanner.Internal;

/// <summary>
/// Vectorized inner loops for template correlation. The dot product over
/// template sized float arrays is the single hottest operation in the reader,
/// executed millions of times per image.
/// </summary>
internal static class MathKernels
{
    /// <summary>Dot product of two equal length arrays.</summary>
    public static float Dot(float[] a, float[] b)
    {
#if NET8_0_OR_GREATER
        // FMA with four accumulators is ~1.5x the Vector<T> codegen here;
        // template arrays are 320 floats, ten full 32-wide steps and no tail.
        if (Fma.IsSupported)
            return DotFma(a, b);
#endif
        int length = Math.Min(a.Length, b.Length);
        int width = Vector<float>.Count;
        var accumulator = Vector<float>.Zero;
        int i = 0;
        for (; i + width <= length; i += width)
            accumulator += new Vector<float>(a, i) * new Vector<float>(b, i);
        float sum = Vector.Dot(accumulator, Vector<float>.One);
        for (; i < length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    /// <summary>
    /// Dot product of one cell against consecutive rows of a flattened
    /// template bank. Processing four rows per pass reuses each cell load
    /// across four FMAs, so the cell stays in registers while the bank
    /// streams sequentially through cache.
    /// </summary>
    public static void DotBatch(float[] cell, float[] bank, int rowLength, int rows, float[] results)
    {
#if NET8_0_OR_GREATER
        if (Fma.IsSupported)
        {
            DotBatchFma(cell, bank, rowLength, rows, results);
            return;
        }
#endif
        int width = Vector<float>.Count;
        for (int r = 0; r < rows; r++)
        {
            int rowBase = r * rowLength;
            var accumulator = Vector<float>.Zero;
            int i = 0;
            for (; i + width <= rowLength; i += width)
                accumulator += new Vector<float>(cell, i) * new Vector<float>(bank, rowBase + i);
            float sum = Vector.Dot(accumulator, Vector<float>.One);
            for (; i < rowLength; i++)
                sum += cell[i] * bank[rowBase + i];
            results[r] = sum;
        }
    }

#if NET8_0_OR_GREATER
    private static void DotBatchFma(float[] cell, float[] bank, int rowLength, int rows, float[] results)
    {
        ref float rc = ref MemoryMarshal.GetArrayDataReference(cell);
        ref float rb = ref MemoryMarshal.GetArrayDataReference(bank);
        int r = 0;
        for (; r + 4 <= rows; r += 4)
        {
            nuint b0 = (nuint)(r * rowLength);
            nuint b1 = b0 + (nuint)rowLength;
            nuint b2 = b1 + (nuint)rowLength;
            nuint b3 = b2 + (nuint)rowLength;
            var acc0 = Vector256<float>.Zero;
            var acc1 = Vector256<float>.Zero;
            var acc2 = Vector256<float>.Zero;
            var acc3 = Vector256<float>.Zero;
            int i = 0;
            for (; i + 8 <= rowLength; i += 8)
            {
                var c = Vector256.LoadUnsafe(ref rc, (nuint)i);
                acc0 = Fma.MultiplyAdd(c, Vector256.LoadUnsafe(ref rb, b0 + (nuint)i), acc0);
                acc1 = Fma.MultiplyAdd(c, Vector256.LoadUnsafe(ref rb, b1 + (nuint)i), acc1);
                acc2 = Fma.MultiplyAdd(c, Vector256.LoadUnsafe(ref rb, b2 + (nuint)i), acc2);
                acc3 = Fma.MultiplyAdd(c, Vector256.LoadUnsafe(ref rb, b3 + (nuint)i), acc3);
            }
            float s0 = Vector256.Sum(acc0);
            float s1 = Vector256.Sum(acc1);
            float s2 = Vector256.Sum(acc2);
            float s3 = Vector256.Sum(acc3);
            for (; i < rowLength; i++)
            {
                float c = cell[i];
                s0 += c * bank[(int)b0 + i];
                s1 += c * bank[(int)b1 + i];
                s2 += c * bank[(int)b2 + i];
                s3 += c * bank[(int)b3 + i];
            }
            results[r] = s0;
            results[r + 1] = s1;
            results[r + 2] = s2;
            results[r + 3] = s3;
        }
        for (; r < rows; r++)
        {
            nuint rowBase = (nuint)(r * rowLength);
            var acc = Vector256<float>.Zero;
            int i = 0;
            for (; i + 8 <= rowLength; i += 8)
                acc = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref rc, (nuint)i), Vector256.LoadUnsafe(ref rb, rowBase + (nuint)i), acc);
            float sum = Vector256.Sum(acc);
            for (; i < rowLength; i++)
                sum += cell[i] * bank[(int)rowBase + i];
            results[r] = sum;
        }
    }

    private static float DotFma(float[] a, float[] b)
    {
        int length = Math.Min(a.Length, b.Length);
        ref float ra = ref MemoryMarshal.GetArrayDataReference(a);
        ref float rb = ref MemoryMarshal.GetArrayDataReference(b);
        var acc0 = Vector256<float>.Zero;
        var acc1 = Vector256<float>.Zero;
        var acc2 = Vector256<float>.Zero;
        var acc3 = Vector256<float>.Zero;
        int i = 0;
        for (; i + 32 <= length; i += 32)
        {
            acc0 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)i), Vector256.LoadUnsafe(ref rb, (nuint)i), acc0);
            acc1 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)(i + 8)), Vector256.LoadUnsafe(ref rb, (nuint)(i + 8)), acc1);
            acc2 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)(i + 16)), Vector256.LoadUnsafe(ref rb, (nuint)(i + 16)), acc2);
            acc3 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)(i + 24)), Vector256.LoadUnsafe(ref rb, (nuint)(i + 24)), acc3);
        }
        for (; i + 8 <= length; i += 8)
            acc0 = Fma.MultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)i), Vector256.LoadUnsafe(ref rb, (nuint)i), acc0);
        float sum = Vector256.Sum(Avx.Add(Avx.Add(acc0, acc1), Avx.Add(acc2, acc3)));
        for (; i < length; i++)
            sum += a[i] * b[i];
        return sum;
    }
#endif
}
