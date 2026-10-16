namespace Scrimshaw.TestSupport;

/// <summary>A temp folder deleted after the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scrimshaw-tests", Guid.NewGuid().ToString("N"));

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
