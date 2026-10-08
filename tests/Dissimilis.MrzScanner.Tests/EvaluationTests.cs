using System.Text.Json;
using MrzHarness;
using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class EvaluationTests
{
    [Fact]
    public void Identical_baseline_and_output_paths_do_not_hide_a_regression()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mrz-evaluate-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            // A generated blank bitmap, not a sample document.
            byte[] bmp = new byte[58];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(54).CopyTo(bmp, 10);
            BitConverter.GetBytes(40).CopyTo(bmp, 14);
            BitConverter.GetBytes(1).CopyTo(bmp, 18);
            BitConverter.GetBytes(1).CopyTo(bmp, 22);
            bmp[26] = 1; bmp[28] = 24;
            File.WriteAllBytes(Path.Combine(directory, "blank.bmp"), bmp);
            File.WriteAllText(Path.Combine(directory, "expected.json"), "{\"blank.bmp\":[]}");
            string metrics = Path.Combine(directory, "metrics.json");
            File.WriteAllText(metrics, JsonSerializer.Serialize(new EvaluateRunner.Metrics
                { Images = 1, Exact = 2 }));
            Assert.Equal(2, EvaluateRunner.Run(directory, metrics, metrics));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
