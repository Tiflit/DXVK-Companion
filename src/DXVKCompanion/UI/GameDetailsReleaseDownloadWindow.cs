using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DXVKCompanion.DXVK;
using DXVKCompanion.Models;

namespace DXVKCompanion.UI;

public sealed class GameDetailsReleaseDownloadWindow : Form
{
    private readonly DxvkManager _manager;
    private readonly GameProfile _profile;
    private readonly Func<bool> _pending;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ReleaseInfo> _releases = new();
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _more = new() { Text = "Load more", AutoSize = true, Enabled = false };
    private readonly Button _download = new() { Text = "Download selected", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(600, 0) };
    private bool _busy;
    private bool _disposed;
    private bool _hasMore;
    private int _nextPage = 1;
    public bool LibraryChanged { get; private set; }

    public GameDetailsReleaseDownloadWindow(DxvkManager manager, GameProfile profile, Func<bool> pending)
    {
        _manager = manager;
        _profile = profile;
        _pending = pending;
        Text = "Official DXVK versions";
        Size = new Size(680, 480);
        MinimumSize = new Size(540, 360);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange(new Control[] { _refresh, _more, _download });
        layout.Controls.Add(_list, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        Controls.Add(layout);
        _refresh.Click += async (_, _) => await LoadPageAsync(refresh: true);
        _more.Click += async (_, _) => await LoadPageAsync(refresh: false);
        _download.Click += async (_, _) => await DownloadAsync();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        // Showing this explicit browser is the user's catalog request. Ordinary Game Details never fetches it.
        Shown += async (_, _) => await LoadPageAsync(refresh: true);
        FormClosing += (_, _) => _lifetime.Cancel();
    }

    private bool Alive => !_disposed && !IsDisposed && !Disposing && !_lifetime.IsCancellationRequested;

    private void UpdateButtons()
    {
        if (!Alive) return;
        _refresh.Enabled = !_busy;
        _more.Enabled = !_busy && _hasMore;
        _download.Enabled = GameDetailsReleaseDownloadPresentation.CanDownload(_busy, _pending(), _list.SelectedIndex >= 0);
    }

    private async Task LoadPageAsync(bool refresh)
    {
        if (_busy || !Alive) return;
        _busy = true;
        UpdateButtons();
        _status.Text = "Loading official Windows releases…";
        int page = refresh ? 1 : _nextPage;
        try
        {
            var result = await _manager.BrowseOfficialReleasesAsync(page, _lifetime.Token);
            var descriptions = await Task.Run(() => result.Releases.Select(Describe).ToArray(), _lifetime.Token);
            if (!Alive) return;
            if (refresh) { _releases.Clear(); _list.Items.Clear(); }
            for (int index = 0; index < result.Releases.Count; index++)
            {
                var release = result.Releases[index];
                if (_releases.Any(r => r.Version == release.Version)) continue;
                _releases.Add(release);
                _list.Items.Add(descriptions[index]);
            }
            _nextPage = page + 1;
            _hasMore = result.HasMore;
            _status.Text = _pending() ? "Pending work: downloads are locked; its captured target is unchanged."
                : "Select a version to download. Downloading does not enable or pin it.";
        }
        catch (OperationCanceledException) { if (Alive) _status.Text = "Catalog request cancelled or timed out. Refresh to retry."; }
        catch { if (Alive) _status.Text = "Official catalog unavailable or invalid. Local versions remain usable; refresh to retry."; }
        finally { _busy = false; UpdateButtons(); }
    }

    private string Describe(ReleaseInfo release) => release.Version + " — " +
        GameDetailsReleaseDownloadPresentation.LocalStatus(_manager.DxvkSourceDir, release.Version, _profile.Api, _profile.Architecture);

    private async Task DownloadAsync()
    {
        if (!GameDetailsReleaseDownloadPresentation.CanDownload(_busy, _pending(), _list.SelectedIndex >= 0) || !Alive) return;
        int index = _list.SelectedIndex;
        var release = _releases[index];
        _busy = true;
        UpdateButtons();
        _status.Text = "Downloading and verifying " + release.Version + "…";
        try
        {
            var result = await _manager.AcquireOfficialReleaseAsync(release.Version, _lifetime.Token);
            LibraryChanged = true;
            string description = await Task.Run(() => Describe(release), _lifetime.Token);
            if (!Alive) return;
            _list.Items[index] = description;
            _status.Text = GameDetailsReleaseDownloadPresentation.CompletedStatus(result);
        }
        catch (OperationCanceledException) { if (Alive) _status.Text = "Download cancelled or timed out. Select Download to retry."; }
        catch { if (Alive) _status.Text = "Download refused or failed verification. Retained releases are unchanged. Retry or inspect local files."; }
        finally { _busy = false; UpdateButtons(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
