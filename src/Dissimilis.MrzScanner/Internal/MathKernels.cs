using System.Numerics;

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
}
