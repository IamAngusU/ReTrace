using ReTrace.Core;

public static class PhotoRecTests
{
    private static int passed;
    public static async Task<int> RunAsync()
    {
        passed = 0;
        var root = Path.Combine(Path.GetTempPath(), "ReTrace-PhotoRecTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CommandArgumentsAreLiteral(root);
            AllFileTypesUseFixedCommand(root);
            AllNumberedOutputDirectoriesAreCounted(root);
            OutputBudgetsApplyAcrossDirectories(root);
            UnexpectedDirectoriesAreRefused(root);
            NestedOutputDirectoriesAreRefused(root);
            ExistingRunDirectoryIsRefused(root);
            PathIndirectionsAreRefused(root);
            await CancellationBeforeStartCreatesNothing(root);
            await UnexpectedArchiveCannotExtract(root);
            await WrongHashCannotExtract(root);
            Console.WriteLine($"PhotoRec: {passed} command, quota, path, cancellation and archive-integrity assertions passed.");
            return passed;
        }
        finally
        {
            // Generated files only, with no user inputs and a GUID-specific root.
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CommandArgumentsAreLiteral(string root)
    {
        var source = Path.Combine(root, "image $(never-execute) & comma,.img");
        var start = PhotoRecEngine.BuildStartInfo(Path.Combine(root, "photorec_win.exe"), source, root, new());
        Check(!start.UseShellExecute && start.CreateNoWindow, "The worker must not invoke a shell.");
        Check(start.ArgumentList[6] == source, "The complete source path must remain one literal argument.");
        Check(start.ArgumentList[7] == "partition_none,options,keep_corrupted_file,paranoid,fileopt,everything,disable,jpg,enable,png,enable,wholespace,search", "Unexpected fixed PhotoRec command.");
        Check(start.Environment["__COMPAT_LAYER"] == "RunAsInvoker", "Offline images must not trigger elevation.");
        Throws<ArgumentException>(() => PhotoRecEngine.ValidateOptions(new() { Types = ["jpg,enable,inter"] }));
    }

    private static void AllFileTypesUseFixedCommand(string root)
    {
        var start = PhotoRecEngine.BuildStartInfo("photorec_win.exe", "image.img", root, new() { Types = ["all"], IncludeBroken = false });
        Check(start.ArgumentList[7] == "partition_none,options,keep_corrupted_file_no,paranoid,fileopt,everything,enable,wholespace,search", "All-formats mode must use a fixed command.");
        Throws<ArgumentException>(() => PhotoRecEngine.ValidateOptions(new() { Types = ["all", "jpg"] }));
        Throws<ArgumentException>(() => PhotoRecEngine.ValidateOptions(new() { Types = [] }));
    }

    private static void AllNumberedOutputDirectoriesAreCounted(string root)
    {
        var run = Directory.CreateDirectory(Path.Combine(root, "rollover")).FullName;
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(run, "recup.1")).FullName, "f0001.jpg"), [1, 2]);
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(run, "recup.2")).FullName, "f0002.png"), [3, 4, 5]);
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(run, "recup.12")).FullName, "f0003.pdf"), [6]);
        var inventory = PhotoRecEngine.InspectOutput(run, new());
        Check(inventory.Files.Count == 3 && inventory.Bytes == 6 && inventory.LimitReason is null, "Rollover directories must be inventoried.");
    }

    private static void OutputBudgetsApplyAcrossDirectories(string root)
    {
        var run = Path.Combine(root, "rollover");
        Check(PhotoRecEngine.InspectOutput(run, new() { MaximumOutputBytes = 5 }).LimitReason == "maximum_output_bytes", "The global output budget must cover every numbered directory.");
        Check(PhotoRecEngine.InspectOutput(run, new() { MaximumFiles = 2 }).LimitReason == "maximum_files", "The file count must cover every numbered directory.");
        Check(PhotoRecEngine.InspectOutput(run, new() { MaximumFileBytes = 2 }).LimitReason == "maximum_file_bytes", "Oversized individual files must stop recovery.");
        Throws<ArgumentException>(() => PhotoRecEngine.ValidateOptions(new() { TimeoutSeconds = 0 }));
    }

    private static void UnexpectedDirectoriesAreRefused(string root)
    {
        var run = Directory.CreateDirectory(Path.Combine(root, "unexpected")).FullName;
        Directory.CreateDirectory(Path.Combine(run, "recup.bad"));
        Check(PhotoRecEngine.InspectOutput(run, new()).LimitReason == "unexpected_output_directory", "Unexpected output directory names must not be trusted.");
    }

    private static void NestedOutputDirectoriesAreRefused(string root)
    {
        var run = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        Directory.CreateDirectory(Path.Combine(run, "recup.1", "unexpected"));
        Check(PhotoRecEngine.InspectOutput(run, new()).LimitReason == "unsafe_output_entry", "Output is expected to contain flat, ordinary files.");
    }

    private static void ExistingRunDirectoryIsRefused(string root) =>
        Throws<IOException>(() => PhotoRecPaths.CreateNewDirectory(root));

    private static void PathIndirectionsAreRefused(string root)
    {
        Throws<IOException>(() => PhotoRecPaths.RequireAbsolute("relative.img"));
        Throws<IOException>(() => PhotoRecPaths.RequireAbsolute("\\\\.\\PhysicalDrive0"));
        Throws<IOException>(() => PhotoRecPaths.RequireAbsolute(Path.Combine(root, "..", "image.img")));
        Throws<IOException>(() => PhotoRecPaths.RequireRegularFile(root));
    }

    private static async Task CancellationBeforeStartCreatesNothing(string root)
    {
        var run = Path.Combine(root, "cancelled-run");
        using var token = new CancellationTokenSource();
        token.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => PhotoRecEngine.RunAsync("not-opened.img", run, cancellationToken: token.Token));
        Check(!Path.Exists(run), "A pre-cancelled job must not create a run.");
    }

    private static async Task UnexpectedArchiveCannotExtract(string root)
    {
        var badArchive = Path.Combine(root, "not-an-upstream-archive.zip");
        File.WriteAllBytes(badArchive, [0x50, 0x4b, 3, 4]);
        var run = Directory.CreateDirectory(Path.Combine(root, "bad-archive")).FullName;
        await ThrowsAsync<InvalidDataException>(() => PhotoRecBundle.EnsureAsync(run, archivePath: badArchive));
        Check(!Directory.Exists(Path.Combine(run, "tools")), "Archive integrity must be checked before extraction.");
    }

    private static async Task WrongHashCannotExtract(string root)
    {
        var badArchive = Path.Combine(root, "same-length-wrong-sha256.zip");
        using (var output = File.Create(badArchive)) output.SetLength(PhotoRecBundle.ArchiveBytes);
        var run = Directory.CreateDirectory(Path.Combine(root, "wrong-hash")).FullName;
        await ThrowsAsync<InvalidDataException>(() => PhotoRecBundle.EnsureAsync(run, archivePath: badArchive));
        Check(!Directory.Exists(Path.Combine(run, "tools")), "A matching archive length cannot bypass the SHA-256 pin.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); passed++; }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { passed++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { passed++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
