using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Syncloom.Services;
using Windows.Storage.Pickers;

namespace Syncloom.Pages;

/// <summary>One row of the list. Strings only, so the XAML bindings stay trivial.</summary>
public sealed record Row(string Id, string Name, string Detail, string Glyph, bool IsFolder, bool IsTrash, bool Writable)
{
    public Visibility CanDownload => !IsFolder && !IsTrash ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsTrashVisible => IsTrash ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CanWrite => Writable && !IsTrash ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class FilesPage : Page
{
    public sealed record Args(string? Parent, string Title, bool Shared, bool Trash = false);

    private readonly Stack<Args> _stack = new();
    private Args _here = new(null, "Files", false);

    public FilesPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _here = (Args)e.Parameter;
        _stack.Clear();
        await LoadAsync();
    }

    private bool Writable => !_here.Trash && !(_here.Shared && _here.Parent is null);

    private async Task LoadAsync()
    {
        TitleText.Text = _here.Title;
        BackButton.Visibility = _stack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Commands.Visibility = Writable ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var op = _here.Trash ? "trash_list" : _here.Shared && _here.Parent is null ? "shared_with_me" : "list";
            var nodes = (await AppState.CallAsync(op, new { parent = _here.Parent }))!.AsArray();
            var rows = nodes.OrderBy(n => n!["kind"]!.GetValue<string>() != "folder").ThenBy(n => n!["name"]!.GetValue<string>(), StringComparer.CurrentCultureIgnoreCase)
                .Select(n => ToRow(n!)).ToList();
            List.ItemsSource = rows;
            EmptyText.Text = _here.Trash ? "The trash is empty" : _here.Shared ? "Nothing shared with you yet" : "This folder is empty. Upload files to get started.";
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Info.IsOpen = false;
        }
        catch (Exception ex) { Info.Message = ex.Message; Info.IsOpen = true; }
    }

    private Row ToRow(JsonNode n)
    {
        var folder = n["kind"]!.GetValue<string>() == "folder";
        var size = n["size"]!.GetValue<long>();
        var detail = folder ? "Folder" : n["head_version"] is null ? "Uploading…" : $"{size / 1024.0 / 1024.0:0.##} MB";
        return new Row(n["id"]!.GetValue<string>(), n["name"]!.GetValue<string>(), detail, folder ? "" : "", folder, _here.Trash, n["access"]!.GetValue<string>() != "ro");
    }

    private async void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Row { IsFolder: true } r && !_here.Trash)
        {
            _stack.Push(_here);
            _here = new Args(r.Id, r.Name, _here.Shared);
            await LoadAsync();
        }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_stack.Count == 0) return;
        _here = _stack.Pop();
        await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task Guard(Func<Task> work)
    {
        try { await work(); await LoadAsync(); }
        catch (Exception ex) { Info.Message = ex.Message; Info.IsOpen = true; }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = "Untitled folder" };
        var dlg = new ContentDialog { Title = "New folder", Content = box, PrimaryButtonText = "Create", CloseButtonText = "Cancel", XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Primary };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) await Guard(() => AppState.CallAsync("create_folder", new { parent = _here.Parent, name = box.Text.Trim() }));
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files is null) return;
        await Guard(async () =>
        {
            foreach (var f in files)
                await AppState.CallAsync("upload_new_file", new { parent = _here.Parent, name = f.Name, path = f.Path, mtime_ms = new DateTimeOffset((await f.GetBasicPropertiesAsync()).DateModified).ToUnixTimeMilliseconds() });
        });
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        var row = ((IEnumerable<Row>)List.ItemsSource).First(r => r.Id == id);
        var picker = new FileSavePicker { SuggestedFileName = row.Name };
        picker.FileTypeChoices.Add("File", new List<string> { Path.GetExtension(row.Name) is { Length: > 0 } ext ? ext : "." });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
        var file = await picker.PickSaveFileAsync();
        if (file is not null) await Guard(() => AppState.CallAsync("download_file", new { id, dest = file.Path }));
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await Guard(() => AppState.CallAsync("trash", new { id = (string)((Button)sender).Tag }));
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Guard(() => AppState.CallAsync("restore", new { id = (string)((Button)sender).Tag }));
}
