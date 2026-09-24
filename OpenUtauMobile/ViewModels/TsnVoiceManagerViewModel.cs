using OpenUtauMobile.Services.Dialogs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    readonly TsnVoiceVoiceInstaller installer = new TsnVoiceVoiceInstaller();

    public TsnVoiceManagerViewModel()
    {
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
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
                    .OrderBy(v => v.Name)
                    .Select(v => new TsnVoiceVoiceItemViewModel(catalog, installer, v))
                    .ToList();
            });
            Voices.Load(items);
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
        RefreshInstalledState();
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
