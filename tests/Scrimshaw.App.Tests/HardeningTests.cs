using Microsoft.Extensions.Time.Testing;
using Scrimshaw.App.Services;
using Scrimshaw.App.ViewModels;
using Scrimshaw.Capture.Interop;
using Scrimshaw.Capture.Targets;
using Scrimshaw.Core.Workspaces;
using Scrimshaw.TestSupport;

namespace Scrimshaw.App.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Recent_list_puts_the_latest_first_without_duplicates_and_is_capped()
    {
        var s = new AppSettings();
        for (int i = 0; i < 12; i++) s = s.WithRecent($@"C:\ws\{i}");
        s = s.WithRecent(@"C:\WS\5"); // same folder, different case
        Assert.Equal(AppSettings.MaxRecent, s.Recent.Count);
        Assert.Equal(@"C:\WS\5", s.Recent[0]);
        Assert.Single(s.Recent, r => r.Equals(@"C:\ws\5", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\ws\11", s.Recent[1]);
    }

    [Fact]
    public void Store_round_trips_and_shrugs_off_a_damaged_file()
    {
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.Sub("settings.json"));
        Assert.Empty(store.Load().Recent);

        store.Save(new AppSettings { Recent = [@"C:\a"], Window = new WindowPlacement(10, 20, 900, 700, true) });
        AppSettings loaded = store.Load();
        Assert.Equal([@"C:\a"], loaded.Recent);
        Assert.Equal(new WindowPlacement(10, 20, 900, 700, true), loaded.Window);

        File.WriteAllText(dir.Sub("settings.json"), "{ nope");
        Assert.Empty(store.Load().Recent);
    }
}

public sealed class WorkspaceOpeningTests : IAsyncDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly InMemoryAppSettingsStore _settings = new();
    private readonly MainViewModel _vm;

    public WorkspaceOpeningTests()
    {
        var monitor = new MonitorInfo(new IntPtr(7), @"\.\DISPLAY1", new RectInt32(0, 0, 1920, 1080), 96, true);
        _vm = new MainViewModel(_dialogs, new ImmediateUi(), new FakeTargets(new MonitorTarget(monitor, 1)),
            new FakeCapture(new FakeTimeProvider(), () => new ScriptedSource(Synthetic.Page(1))), appSettings: _settings);
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _dir.Dispose();
    }

    private string MakeWorkspace(string name)
    {
        string path = _dir.Sub(name);
        Workspace.Create(path).Dispose();
        return path;
    }

    [Fact]
    public void Opened_workspaces_go_to_the_top_of_the_recent_list()
    {
        string a = MakeWorkspace("a"), b = MakeWorkspace("b");
        _vm.Open(a);
        _vm.Open(b);
        Assert.Equal([b, a], _vm.RecentWorkspaces);
        Assert.True(_vm.HasRecent);
        Assert.Equal([b, a], _settings.Load().Recent);
    }

    [Fact]
    public void Recent_list_skips_folders_that_no_longer_exist()
    {
        string a = MakeWorkspace("a");
        _settings.Save(new AppSettings { Recent = [_dir.Sub("gone"), a] });
        var vm = new MainViewModel(_dialogs, new ImmediateUi(), new FakeTargets(), new FakeCapture(new FakeTimeProvider(), () => new ScriptedSource(Synthetic.Page(1))), appSettings: _settings);
        Assert.Equal([a], vm.RecentWorkspaces);
    }

    [Fact]
    public void Opening_a_missing_recent_folder_removes_it()
    {
        _settings.Save(new AppSettings { Recent = [_dir.Sub("gone")] });
        _vm.OpenRecentCommand.Execute(_dir.Sub("gone"));
        Assert.Contains("no longer exists", Assert.Single(_dialogs.Errors), StringComparison.Ordinal);
        Assert.Empty(_settings.Load().Recent);
    }

    [Fact]
    public void A_damaged_manifest_offers_repair_and_keeps_frames()
    {
        string a = MakeWorkspace("a");
        File.WriteAllText(Path.Combine(a, Workspace.ManifestFileName), "{ broken");
        _dialogs.Answers.Enqueue(true);

        _vm.Open(a);

        Assert.Contains("boxes and labels", Assert.Single(_dialogs.Questions), StringComparison.Ordinal);
        Assert.True(_vm.HasWorkspace);
        Assert.StartsWith("Workspace repaired", _vm.StatusMessage, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(a, "manifest.json.damaged-*"));
    }

    [Fact]
    public void Declining_repair_leaves_the_workspace_closed_and_untouched()
    {
        string a = MakeWorkspace("a");
        File.WriteAllText(Path.Combine(a, Workspace.SettingsFileName), "{ broken");
        _dialogs.Answers.Enqueue(false);

        _vm.Open(a);

        Assert.Contains("settings go back to their defaults", Assert.Single(_dialogs.Questions), StringComparison.Ordinal);
        Assert.False(_vm.HasWorkspace);
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(a, Workspace.SettingsFileName)));
    }

    [Fact]
    public void A_workspace_open_elsewhere_is_explained()
    {
        string a = MakeWorkspace("a");
        using Workspace elsewhere = Workspace.Open(a, new Imaging.WicImageCodec());
        _vm.Open(a);
        Assert.False(_vm.HasWorkspace);
        Assert.Contains("already open in another Scrimshaw window", Assert.Single(_dialogs.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Reopening_the_current_workspace_is_a_no_op()
    {
        string a = MakeWorkspace("a");
        _vm.Open(a);
        _vm.Open(a);
        Assert.Empty(_dialogs.Errors);
        Assert.Equal("That workspace is already open.", _vm.StatusMessage);
    }
}
