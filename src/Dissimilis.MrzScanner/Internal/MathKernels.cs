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

#if NET8_0_OR_GREATER
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
