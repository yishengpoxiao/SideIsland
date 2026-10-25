using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexIsland
{
    internal sealed class TextTemplatePanel : StackPanel, IDisposable
    {
        private readonly bool demo;
        private readonly TextTemplateStore store;
        private readonly Action<string> copier;
        private readonly Func<DateTime> clock;
        private readonly TextBox search = ToolStyle.Input(), name = ToolStyle.Input(), body = ToolStyle.Input(), preview = ToolStyle.Input();
        private readonly StackPanel list = new StackPanel(), editor = new StackPanel();
        private readonly TextBlock status = ToolStyle.Hint("");
        private readonly DispatcherTimer previewTimer = new DispatcherTimer();
        private List<TextTemplateEntry> entries = new List<TextTemplateEntry>();
        private string selectedId;
        private volatile bool disposed;
        private bool loading, busy, updating, dirty;
        private readonly Task pendingLoad;
        internal IEnumerable<string> TemplateNames { get { return entries.Select(item => item.Name).ToArray(); } }
        internal bool HasUnsavedChanges { get { return dirty; } }
        internal Task PendingLoad { get { return pendingLoad; } }
        internal TextTemplatePanel(bool demoMode) : this(demoMode, null, null, null) { }
        internal TextTemplatePanel(bool demoMode, TextTemplateStore templateStore, Action<string> copy, Func<DateTime> now)
        {
            demo = demoMode; store = templateStore ?? new TextTemplateStore(demoMode); copier = copy ?? Clipboard.SetText; clock = now ?? (() => DateTime.Now);
            Children.Add(ToolStyle.Heading("常用文本模板"));
            Children.Add(ToolStyle.Hint("保存常用回复、提示词和代码片段。复制只写入剪贴板，不会自动粘贴到其他软件。"));
            search.MaxLength = 256; UiLocalization.ToolTip(search, "搜索模板名称或正文"); search.TextChanged += (s, e) => Rebuild(); Children.Add(search); Children.Add(list);
            var create = PanelActionLayout.Create(1, IslandWindow.Button("新建 / 清空草稿", () => Select(null))); create.Margin = new Thickness(0, 6, 0, 6); Children.Add(create);
            editor.Children.Add(ToolStyle.Hint("模板名称")); name.MaxLength = TextTemplateStore.MaximumNameLength; editor.Children.Add(name);
            editor.Children.Add(ToolStyle.Hint("模板正文")); body.MaxLength = TextTemplateStore.MaximumBodyLength; body.AcceptsReturn = true; body.TextWrapping = TextWrapping.Wrap; body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; body.MinHeight = 100; body.MaxHeight = 240; editor.Children.Add(body);
            editor.Children.Add(ToolStyle.Hint("日期占位符：{date}、{time}、{datetime}、{yyyy}、{MM}、{dd}、{HH}、{mm}、{ss}。未知占位符保持原样。"));
            editor.Children.Add(PanelActionLayout.Create(2, IslandWindow.Button("保存模板", SaveClicked), IslandWindow.Button("删除模板", DeleteClicked)));
            editor.Children.Add(ToolStyle.Hint("实时预览")); preview.IsReadOnly = true; preview.AcceptsReturn = true; preview.TextWrapping = TextWrapping.Wrap; preview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; preview.MinHeight = 56; preview.MaxHeight = 180; editor.Children.Add(preview);
            var copyPreview = PanelActionLayout.Create(1, IslandWindow.Button("复制预览", () => CopyText(TextTemplateRenderer.Render(body.Text, clock())))); copyPreview.Margin = new Thickness(0, 6, 0, 0); editor.Children.Add(copyPreview);
            Children.Add(ToolStyle.Card(editor)); Children.Add(status);
            name.TextChanged += (s, e) => { if (!updating) dirty = true; };
            body.TextChanged += (s, e) => { if (!updating) dirty = true; UpdatePreview(); };
            previewTimer.Interval = TimeSpan.FromSeconds(1); previewTimer.Tick += (s, e) => UpdatePreview();
            Loaded += (s, e) => SyncPreviewTimer(); Unloaded += (s, e) => SyncPreviewTimer(true); IsVisibleChanged += (s, e) => SyncPreviewTimer();
            pendingLoad = LoadAsync(); UpdatePreview();
        }
        private async Task LoadAsync()
        {
            loading = true; editor.IsEnabled = false;
            List<TextTemplateEntry> loaded = null; Exception failure = null;
            try
            {
                loaded = await Task.Run(() => store.Load()).ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; }
            if (disposed || Dispatcher.HasShutdownStarted) return;
            try
            {
                await Dispatcher.InvokeAsync(new Action(() => {
                    if (disposed) return;
                    if (failure == null) { entries = loaded; Rebuild(); status.Text = store.LoadFailed ? "模板文件无法读取，原文件已保留；保存新内容前会创建备份。" : "模板仅保存在本机。"; }
                    else status.Text = "读取模板失败，请稍后重试。";
                    loading = false; editor.IsEnabled = true;
                })).Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException) { }
        }
        internal string FindRendered(string templateName)
        { var item = entries.FirstOrDefault(value => String.Equals(value.Name, templateName, StringComparison.OrdinalIgnoreCase)); return item == null ? null : TextTemplateRenderer.Render(item.Body, clock()); }
        internal bool CopyTemplate(string templateName)
        { string rendered = FindRendered(templateName); return rendered != null && CopyText(rendered); }
        private bool CopyText(string text)
        {
            if (disposed || loading || busy) return false;
            if (demo) { status.Text = "演示模式不修改剪贴板。"; return false; }
            try { copier(text); status.Text = "已复制模板内容。"; return true; }
            catch (Exception) { status.Text = "剪贴板正忙，请重试。"; return false; }
        }
        private void Select(TextTemplateEntry item)
        {
            if (disposed || loading || busy) return;
            if (item != null && dirty && selectedId != item.Id) { status.Text = "当前草稿尚未保存，请先保存或新建清空后再切换。"; return; }
            selectedId = item == null ? null : item.Id; updating = true;
            try { name.Text = item == null ? "" : item.Name; body.Text = item == null ? "" : item.Body; dirty = false; }
            finally { updating = false; }
            UpdatePreview();
        }
        private void SyncPreviewTimer(bool unloading = false)
        {
            if (!unloading && !disposed && IsLoaded && IsVisible) { UpdatePreview(); if (!previewTimer.IsEnabled) previewTimer.Start(); }
            else previewTimer.Stop();
        }
        private void UpdatePreview()
        {
            if (disposed) return; string rendered = TextTemplateRenderer.Render(body.Text, clock());
            if (!String.Equals(preview.Text, rendered, StringComparison.Ordinal)) preview.Text = rendered;
        }
        private async void SaveClicked() { await SaveCurrentAsync(); }
        private async void DeleteClicked() { await DeleteCurrentAsync(); }
        internal async Task<bool> SaveCurrentAsync()
        {
            if (disposed || loading || busy) return false;
            var candidate = entries.Select(item => item.Copy()).ToList(); string id = selectedId ?? Guid.NewGuid().ToString("N");
            var value = new TextTemplateEntry { Id = id, Name = name.Text.Trim(), Body = body.Text, UpdatedUtc = clock().ToUniversalTime() };
            int index = candidate.FindIndex(item => item.Id == id); if (index < 0) candidate.Add(value); else candidate[index] = value;
            if (!await CommitAsync(candidate)) return false;
            selectedId = id; dirty = false; Rebuild(); status.Text = "模板已保存。"; return true;
        }
        internal async Task<bool> DeleteCurrentAsync()
        {
            if (disposed || loading || busy || selectedId == null) return false;
            var candidate = entries.Where(item => item.Id != selectedId).Select(item => item.Copy()).ToList();
            if (!await CommitAsync(candidate)) return false;
            Select(null); Rebuild(); status.Text = "模板已删除。"; return true;
        }
        private async Task<bool> CommitAsync(List<TextTemplateEntry> values)
        {
            try { TextTemplateStore.Validate(values); }
            catch (Exception error) { status.Text = error.Message; return false; }
            busy = true; editor.IsEnabled = false;
            try
            {
                await Task.Run(() => store.Save(values)); if (disposed) return false;
                entries = values; return true;
            }
            catch (Exception error) { if (!disposed) status.Text = UiLocalization.Localize("保存模板失败：") + error.Message; return false; }
            finally { busy = false; if (!disposed) editor.IsEnabled = true; }
        }
        private void Rebuild()
        {
            if (disposed) return; list.Children.Clear(); string term = search.Text.Trim();
            foreach (var item in entries.Where(value => term.Length == 0 || value.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 || value.Body.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var selected = item;
                var row = PanelActionLayout.Create(2, IslandWindow.Button(item.Name, () => Select(selected), false), IslandWindow.Button("复制", () => CopyTemplate(selected.Name))); row.Margin = new Thickness(0, 6, 0, 0); list.Children.Add(row);
            }
            if (list.Children.Count == 0) list.Children.Add(ToolStyle.Hint("没有匹配的文本模板。"));
        }
        public void Dispose() { disposed = true; previewTimer.Stop(); }
    }
}
