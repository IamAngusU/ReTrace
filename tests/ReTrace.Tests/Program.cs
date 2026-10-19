using System.Text.Json;
using ReTrace.Tests;
using ReTrace.Core;

var checks = new Dictionary<string, int>();
if (args is ["--list-volumes"])
{
    Console.WriteLine(JsonSerializer.Serialize(VolumeAcquisition.ListVolumes()));
    return 0;
}
if (args is ["--scan-recycle", var copiedBin])
{
    Console.WriteLine(JsonSerializer.Serialize(await RecycleBinRecovery.ScanAsync(Path.GetFullPath(copiedBin))));
    return 0;
}
if (args is ["--scan-windows-recycle"])
{
    Console.WriteLine(JsonSerializer.Serialize(await RecycleBinRecovery.ScanAsync()));
    return 0;
}
if (args is ["--scan-media", var mediaSource])
{
    Console.WriteLine(JsonSerializer.Serialize(await MediaCarver.ScanAsync(Path.GetFullPath(mediaSource))));
    return 0;
}
if (args is ["--scan-media-folder", var mediaFolder])
{
    Console.WriteLine(JsonSerializer.Serialize(await FolderRecovery.ScanAsync(Path.GetFullPath(mediaFolder),
        new() { Types = ["jpeg", "png", "mp4", "mov", "avi"] })));
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
if (args is ["--write-video-fixture", var videoDestination])
{
    using var output = new FileStream(Path.GetFullPath(videoDestination), FileMode.CreateNew, FileAccess.Write, FileShare.None);
    output.Write(new byte[1048570]);
    output.Write(VideoCarverTests.Mp4());
    output.Write(new byte[19]);
    output.Write(VideoCarverTests.Avi());
    return 0;
}
try
{
    checks["carver"] = CarverTests.Run();
    checks["video_carver"] = await VideoCarverTests.RunAsync();
    checks["media_integration"] = await MediaIntegrationTests.RunAsync();
    checks["export"] = ExportTests.Run();
    checks["volume_capture"] = await VolumeTests.RunAsync();
    checks["photorec"] = await PhotoRecTests.RunAsync();
    checks["large_images"] = await LargeImageTests.RunAsync();
    checks["investigations"] = await InvestigationTests.RunAsync();
    checks["folders"] = await FolderRecoveryTests.RunAsync();
    checks["recycle_bin"] = await RecycleBinTests.RunAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", assertions = checks.Values.Sum(), suites = checks }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", suites = checks }));
    return 1;
}
