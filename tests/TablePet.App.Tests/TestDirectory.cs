using System.IO;
using System.Runtime.ExceptionServices;

namespace TablePet.App.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "TablePet.App.Tests", Guid.NewGuid().ToString("N"));

    public TestDirectory() => Directory.CreateDirectory(Root);
    public string FilePath(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        // Root is a private freshly-created GUID directory, never a caller-supplied location.
        Directory.Delete(Root, recursive: true);
    }
}

internal static class Sta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
