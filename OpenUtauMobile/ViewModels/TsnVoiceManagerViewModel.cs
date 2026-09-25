using OpenUtauMobile.Services.Dialogs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DynamicData.Binding;
using OpenUtau.Core.TsnVoice;
using OpenUtauMobile.Helpers;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace OpenUtauMobile.ViewModels;

/// <summary>
/// TSNVOICE 语音管理器：按内嵌目录列出 TsnVoice 语音，
/// 支持版本选择、下载安装与卸载，对应参考实现语音管理器的核心流程。
/// </summary>
public class TsnVoiceManagerViewModel : ReactiveObject
{
    [Reactive] public ObservableCollectionExtended<TsnVoiceVoiceItemViewModel> Voices { get; set; } = [];
    [Reactive] public bool IsLoading { get; set; }
    [Reactive] public string ErrorMessage { get; set; } = string.Empty;
    [Reactive] public string SearchText { get; set; } = string.Empty;

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    readonly TsnVoiceVoiceInstaller installer = new TsnVoiceVoiceInstaller();
    List<TsnVoiceVoiceItemViewModel> allVoices = new();

    public TsnVoiceManagerViewModel()
    {
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        this.WhenAnyValue(x => x.SearchText)
            .Subscribe(_ => ApplyFilter());
    }

    void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            Voices.Load(allVoices);
            return;
        }
        string query = SearchText.Trim();
        Voices.Load(allVoices.Where(item =>
            item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || item.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || item.Language.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            List<TsnVoiceVoiceItemViewModel> items = await Task.Run(() =>
            {
                TsnVoiceCatalog catalog = TsnVoiceCatalog.Instance;
                return catalog.Voices
                    .Where(v => v.Id.IndexOf("_tts", StringComparison.OrdinalIgnoreCase) < 0)
                    .OrderBy(v => v.Name)
                    .Select(v => new TsnVoiceVoiceItemViewModel(catalog, installer, v))
                    .ToList();
            });
            allVoices = items;
            ApplyFilter();
            await Task.Run(() =>
            {
                foreach (TsnVoiceVoiceItemViewModel item in items)
                {
                    item.LoadPortrait();
                }
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = string.Format(L.S("TsnVoice.LoadFailed"), ex.Message);
            Serilog.Log.Error(ex, "TSNVOICE 目录加载失败");
            ToastService.Enqueue(string.Format(L.S("TsnVoice.LoadFailed"),
                ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public class TsnVoiceVoiceItemViewModel : ReactiveObject
{
    readonly TsnVoiceCatalog catalog;
    readonly TsnVoiceVoiceInstaller installer;
    readonly TsnVoiceCatalogEntry entry;

    public string Id => entry.Id;
    public string Name => entry.Name;
    public string Language => entry.Language;

    public List<TsnVoiceCatalogVersion> Versions => entry.Versions;

    [Reactive] public TsnVoiceCatalogVersion SelectedVersion { get; set; }

    [Reactive] public bool IsSelectedInstalled { get; set; }
    [Reactive] public bool HasUpdate { get; set; }
    [Reactive] public string InstalledText { get; set; } = string.Empty;

    [Reactive] public bool IsBusy { get; set; }
    [Reactive] public int Progress { get; set; }

    [Reactive] public Bitmap? AvatarBitmap { get; set; }
    public bool HasAvatar => AvatarBitmap != null;

    static readonly HttpClient portraitHttp = CreatePortraitHttp();
    static readonly ConcurrentDictionary<string, byte[]> portraitCache = new();

    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public TsnVoiceVoiceItemViewModel(
        TsnVoiceCatalog catalog,
        TsnVoiceVoiceInstaller installer,
        TsnVoiceCatalogEntry entry)
    {
        this.catalog = catalog;
        this.installer = installer;
        this.entry = entry;
        SelectedVersion = entry.Versions.FirstOrDefault();

        IObservable<bool> canDownload = this
            .WhenAnyValue(x => x.IsBusy, x => x.SelectedVersion)
            .Select(state => !state.Item1 && state.Item2 != null);
        DownloadCommand = ReactiveCommand.CreateFromTask(OnDownloadAsync, canDownload);

        IObservable<bool> canRemove = this
            .WhenAnyValue(x => x.IsBusy, x => x.IsSelectedInstalled)
            .Select(state => !state.Item1 && state.Item2);
        RemoveCommand = ReactiveCommand.CreateFromTask(OnRemoveAsync, canRemove);

        this.WhenAnyValue(x => x.SelectedVersion)
            .Subscribe(_ => RefreshInstalledState());
        this.WhenAnyValue(x => x.AvatarBitmap)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasAvatar)));
        RefreshInstalledState();
    }

    static HttpClient CreatePortraitHttp() {
        HttpClient client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenUtauMobile-TsnVoice/1.0");
        return client;
    }

    /// <summary>
    /// 加载卡片立绘：已下载的本地文件 → 内嵌目录立绘 → 远端地址（内存缓存）。
    /// 后台线程调用，失败保持占位图标。
    /// </summary>
    public void LoadPortrait() {
        if (AvatarBitmap != null) {
            return;
        }
        try {
            string local = TsnVoiceVoiceInstaller.PortraitPath(
                installer.VoiceRoot, entry.Id);
            if (File.Exists(local)) {
                SetBitmap(File.ReadAllBytes(local));
                if (AvatarBitmap != null) {
                    return;
                }
            }
            byte[] embedded = catalog.GetPortraitBytes(entry);
            if (embedded is { Length: > 0 }) {
                SetBitmap(embedded);
                if (AvatarBitmap != null) {
                    return;
                }
            }
            if (!string.IsNullOrEmpty(entry.ImageUrl)
                && portraitCache.TryGetValue(entry.Id, out byte[] cached)) {
                SetBitmap(cached);
                return;
            }
            if (!string.IsNullOrEmpty(entry.ImageUrl)) {
                byte[] remote = portraitHttp.GetByteArrayAsync(entry.ImageUrl)
                    .GetAwaiter().GetResult();
                if (remote is { Length: > 0 }) {
                    portraitCache[entry.Id] = remote;
                    SetBitmap(remote);
                }
            }
        } catch {
        }
    }

    void SetBitmap(byte[] data) {
        try {
            using MemoryStream stream = new(data);
            AvatarBitmap = new Bitmap(stream);
        } catch {
        }
    }

    public void RefreshInstalledState()
    {
        List<string> installed = installer.InstalledLabels(catalog, entry);
        IsSelectedInstalled = SelectedVersion != null
            && installer.IsInstalled(catalog, entry, SelectedVersion);
        string latest = entry.Versions.FirstOrDefault()?.Version ?? string.Empty;
        HasUpdate = installed.Count > 0
            && !string.IsNullOrEmpty(latest)
            && !installer.InstalledVersions(catalog, entry).Contains(latest);
        InstalledText = installed.Count == 0
            ? string.Empty
            : string.Format(L.S("TsnVoice.InstalledVersions"), string.Join(", ", installed));
    }

    async Task OnDownloadAsync()
    {
        TsnVoiceCatalogVersion version = SelectedVersion;
        if (version == null)
        {
            return;
        }
        IsBusy = true;
        Progress = 0;
        try
        {
            Progress<double> progress = new(fraction =>
            {
                Progress = (int)Math.Round(fraction * 100);
            });
            await installer.DownloadAsync(catalog, entry, version, progress,
                CancellationToken.None);
            ToastService.Enqueue(string.Format(
                L.S("TsnVoice.DownloadSuccess"), entry.Name, version.Label));
            AvatarBitmap = null;
            LoadPortrait();
        }
        catch (Exception ex)
        {
            ToastService.Enqueue(string.Format(
                L.S("TsnVoice.DownloadFailed"), ex.Message));
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            RefreshInstalledState();
        }
    }

    async Task OnRemoveAsync()
    {
        TsnVoiceCatalogVersion version = SelectedVersion;
        if (version == null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            await Task.Run(() => installer.Remove(catalog, entry, version));
            ToastService.Enqueue(string.Format(
                L.S("TsnVoice.Removed"), entry.Name, version.Label));
        }
        catch (Exception ex)
        {
            ToastService.Enqueue(string.Format(
                L.S("TsnVoice.RemoveFailed"), ex.Message));
        }
        finally
        {
            IsBusy = false;
            RefreshInstalledState();
        }
    }
}
