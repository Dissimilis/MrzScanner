using System.Diagnostics;
using System.Numerics;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
using Dissimilis.MrzScanner.Internal;

namespace MrzHarness;

/// <summary>
/// Micro benchmark for the template dot product: the current Vector path
/// against an explicit AVX2/FMA implementation, on template sized arrays.
/// </summary>
internal static class BenchRunner
{
    public static int Run()
    {
        Console.WriteLine($"Vector.IsHardwareAccelerated: {Vector.IsHardwareAccelerated}, Vector<float>.Count: {Vector<float>.Count}");
#if NET8_0_OR_GREATER
        Console.WriteLine($"Avx2: {Avx2.IsSupported}, Fma: {Fma.IsSupported}");
#endif
        int length = OcrTemplates.Width * OcrTemplates.Height;
        var rng = new Random(42);
        var a = new float[length];
        var banks = new float[64][];
        for (int i = 0; i < length; i++)
            a[i] = (float)rng.NextDouble() - 0.5f;
        for (int b = 0; b < banks.Length; b++)
        {
            banks[b] = new float[length];
            for (int i = 0; i < length; i++)
                banks[b][i] = (float)rng.NextDouble() - 0.5f;
        }

        const int iterations = 2_000_000;
        float sink = 0;

        // Warmup both paths.
        for (int i = 0; i < 10_000; i++)
        {
            sink += MathKernels.Dot(a, banks[i % banks.Length]);
            sink += DotFma(a, banks[i % banks.Length]);
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            sink += MathKernels.Dot(a, banks[i & 63]);
        sw.Stop();
        Console.WriteLine($"Vector<float> path: {sw.Elapsed.TotalMilliseconds:F0} ms ({sw.Elapsed.TotalNanoseconds / iterations:F1} ns/dot)");

        sw.Restart();
        for (int i = 0; i < iterations; i++)
            sink += DotFma(a, banks[i & 63]);
        sw.Stop();
        Console.WriteLine($"AVX2/FMA path:      {sw.Elapsed.TotalMilliseconds:F0} ms ({sw.Elapsed.TotalNanoseconds / iterations:F1} ns/dot)");

        Console.WriteLine($"sink {sink}");
        return 0;
    }

#if NET8_0_OR_GREATER
    /// <summary>Explicit AVX2/FMA dot with four accumulators; 320 floats is exactly 10 unrolled steps.</summary>
    private static unsafe float DotFma(float[] a, float[] b)
    {
        if (!Fma.IsSupported)
            return MathKernels.Dot(a, b);
        int length = Math.Min(a.Length, b.Length);
        fixed (float* pa = a, pb = b)
        {
            var acc0 = Vector256<float>.Zero;
            var acc1 = Vector256<float>.Zero;
            var acc2 = Vector256<float>.Zero;
            var acc3 = Vector256<float>.Zero;
            int i = 0;
            for (; i + 32 <= length; i += 32)
            {
                acc0 = Fma.MultiplyAdd(Avx.LoadVector256(pa + i), Avx.LoadVector256(pb + i), acc0);
                acc1 = Fma.MultiplyAdd(Avx.LoadVector256(pa + i + 8), Avx.LoadVector256(pb + i + 8), acc1);
                acc2 = Fma.MultiplyAdd(Avx.LoadVector256(pa + i + 16), Avx.LoadVector256(pb + i + 16), acc2);
                acc3 = Fma.MultiplyAdd(Avx.LoadVector256(pa + i + 24), Avx.LoadVector256(pb + i + 24), acc3);
            }
            var acc = Avx.Add(Avx.Add(acc0, acc1), Avx.Add(acc2, acc3));
            float sum = Vector256.Sum(acc);
            for (; i < length; i++)
                sum += pa[i] * pb[i];
            return sum;
        }
    }
#else
    private static float DotFma(float[] a, float[] b) => MathKernels.Dot(a, b);
#endif
}
