using System.Text.Json;
using ReTrace.Tests;
using ReTrace.Core;

var checks = new Dictionary<string, int>();
if (args is ["--list-volumes"])
{
    Console.WriteLine(JsonSerializer.Serialize(VolumeAcquisition.ListVolumes()));
    return 0;
}
if (args is ["--write-fixture", var destination])
{
    var path = Path.GetFullPath(destination);
    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    output.Write(new byte[4096]);
    output.Write(CarverFixtures.Png());
    output.Write(new byte[4096]);
    output.Write(CarverFixtures.Png()[..^12]);
    return 0;
}
try
{
    checks["carver"] = CarverTests.Run();
    checks["export"] = ExportTests.Run();
    checks["volume_capture"] = await VolumeTests.RunAsync();
    checks["photorec"] = await PhotoRecTests.RunAsync();
    checks["large_images"] = await LargeImageTests.RunAsync();
    checks["investigations"] = await InvestigationTests.RunAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", assertions = checks.Values.Sum(), suites = checks }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", suites = checks }));
    return 1;
}
