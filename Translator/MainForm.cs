using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Translator.Models;
using Translator.Services;

namespace Translator
{
    public class MainForm : Form
    {
        private readonly EntryStore _store = new EntryStore();
        private readonly AppSettings _settings = AppSettings.Load();
        private ArgosTranslateService _translator;

        private string _targetLanguage = Languages.Default;
        private bool _showOriginal;
        private CancellationTokenSource _cts;

        private MenuStrip _menu;
        private ToolStripMenuItem _translateMenu;
        private ToolStripMenuItem _showMenu;
        private ToolStripMenuItem _showTranslatedItem;
        private ToolStripMenuItem _showOriginalItem;
        private DataGridView _grid;
        private TextBox _input;
        private Button _addButton;
        private Button _removeButton;
        private Button _cancelButton;
        private ToolTip _toolTip;
        private ToolStripProgressBar _progressBar;
        private ToolStripStatusLabel _statusLabel;

        public MainForm()
        {
            CreateTranslator();
            BuildUi();
            Load += async (s, e) => await ConnectAsync();
            FormClosing += (s, e) => _cts?.Cancel();
            // The Argos Translate server is run separately (ArgosTranslateServer\start.cmd / stop.cmd):
            // the app only sends requests to it and never starts, stops or configures it
        }

        private void BuildUi()
        {
            Text = "Translator";
            Width = 900;
            Height = 600;

            _menu = new MenuStrip();

            // Nav: File menu for large data sets
            var fileMenu = new ToolStripMenuItem("File");
            fileMenu.DropDownItems.Add("Import (TXT / CSV)...", null, async (s, e) => await ImportAsync());
            fileMenu.DropDownItems.Add("Export to CSV...", null, (s, e) => Export());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Clear All Items", null, (s, e) => ClearAll());
            fileMenu.DropDownItems.Add("Clear Translation Cache", null, (s, e) => ClearCache());
            fileMenu.DropDownItems.Add("Reconnect to Server", null, async (s, e) => await ConnectAsync());
            fileMenu.MouseHover += (s, e) => fileMenu.ShowDropDown();
            _menu.Items.Add(fileMenu);

            // Nav: "Translate" menu that opens on hover and lists the languages in Languages.Targets
            _translateMenu = new ToolStripMenuItem("Translate");
            foreach (var lang in Languages.Targets)
            {
                var item = new ToolStripMenuItem(lang.Value) { Tag = lang.Key };
                item.Click += async (s, e) => await SetLanguageAsync((string)((ToolStripItem)s).Tag);
                _translateMenu.DropDownItems.Add(item);
            }
            _translateMenu.MouseHover += (s, e) => _translateMenu.ShowDropDown();
            _menu.Items.Add(_translateMenu);

            // Nav: server settings (address, parallel requests)
            var settingsItem = new ToolStripMenuItem("Settings");
            settingsItem.Click += async (s, e) => await EditSettingsAsync();
            _menu.Items.Add(settingsItem);

            var checkItem = new ToolStripMenuItem("Check Connection");
            checkItem.Click += (s, e) =>
            {
                using var form = new ConnectionCheckForm(_settings);
                form.ShowDialog(this);
            };
            _menu.Items.Add(checkItem);

            // Nav: "Show" menu to switch the table between translated and original text
            _showMenu = new ToolStripMenuItem("Show");
            _showTranslatedItem = new ToolStripMenuItem("Translated Text");
            _showOriginalItem = new ToolStripMenuItem("Original Text");
            _showTranslatedItem.Click += (s, e) => SetShowOriginal(false);
            _showOriginalItem.Click += (s, e) => SetShowOriginal(true);
            _showMenu.DropDownItems.Add(_showTranslatedItem);
            _showMenu.DropDownItems.Add(_showOriginalItem);
            _showMenu.MouseHover += (s, e) => _showMenu.ShowDropDown();
            _menu.Items.Add(_showMenu);

            MainMenuStrip = _menu;

            // Table of words / sentences / paragraphs
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells, // fast with large data sets
                ShowCellToolTips = false // the ToolTip below is used instead (it supports long text)
            };
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.Columns.Add("Type", "Type");
            _grid.Columns.Add("Text", "Translated Text");
            _grid.Columns["Type"].FillWeight = 12;
            _grid.Columns["Text"].FillWeight = 88;
            _grid.CellMouseEnter += Grid_CellMouseEnter;
            _grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

            _toolTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 300, ReshowDelay = 100 };

            // Input area
            _input = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

            _addButton = new Button { Text = "Add", Dock = DockStyle.Top };
            _addButton.Click += async (s, e) => await AddAsync();

            _cancelButton = new Button { Text = "Cancel", Dock = DockStyle.Top, Enabled = false };
            _cancelButton.Click += (s, e) => _cts?.Cancel();

            _removeButton = new Button { Text = "Remove", Dock = DockStyle.Bottom };
            _removeButton.Click += (s, e) => RemoveSelected();

            var buttons = new Panel { Dock = DockStyle.Right, Width = 100 };
            buttons.Controls.Add(_cancelButton);
            buttons.Controls.Add(_addButton);
            buttons.Controls.Add(_removeButton);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 90 };
            bottom.Controls.Add(_input);
            bottom.Controls.Add(buttons);

            // Status bar with progress
            var status = new StatusStrip();
            _progressBar = new ToolStripProgressBar { Width = 200, Visible = false };
            _statusLabel = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            status.Items.Add(_progressBar);
            status.Items.Add(_statusLabel);

            Controls.Add(_grid);
            Controls.Add(bottom);
            Controls.Add(_menu);
            Controls.Add(status);

            UpdateTitle();
        }

        private void UpdateTitle()
        {
            var workers = _translator.Workers > 0 ? $", {_translator.Workers} worker{(_translator.Workers == 1 ? "" : "s")}" : "";
            Text = "Translator - " + Languages.GetName(_targetLanguage) + " (" + _translator.Name + workers + ")";
            _grid.Columns["Text"].HeaderText = _showOriginal
                ? "Original Text"
                : "Translated Text (" + Languages.GetName(_targetLanguage) + ")";
            foreach (ToolStripMenuItem item in _translateMenu.DropDownItems)
                item.Checked = (string)item.Tag == _targetLanguage;
            _showTranslatedItem.Checked = !_showOriginal;
            _showOriginalItem.Checked = _showOriginal;
        }

        // ------------------------------------------------------------------ service

        private void CreateTranslator()
        {
            if (_translator != null) _translator.StatusChanged -= OnTranslatorStatus;
            _translator = _settings.CreateService();
            _translator.StatusChanged += OnTranslatorStatus;
        }

        private void OnTranslatorStatus(string message)
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(() => _statusLabel.Text = message));
        }

        /// <summary>
        /// (Re)connects to the server: reads its languages and workers, then translates the list.
        /// The server runs separately (ArgosTranslateServer\start.cmd); the app only connects to it.
        /// </summary>
        private async Task ConnectAsync()
        {
            CreateTranslator();
            _statusLabel.Text = "Connecting to " + _settings.ServerUrl + "...";
            if (!await IsServerReachableAsync())
            {
                UpdateTitle();
                _statusLabel.Text = $"The translation server is not reachable at {_settings.ServerUrl}. " +
                                    @"Start it with ArgosTranslateServer\start.cmd, then use File > Reconnect.";
                return;
            }
            await UpdateLanguageMenuAsync();
            UpdateTitle();
            await RefreshAsync();
        }

        /// <summary>GET /languages answers (the load balancer answers it once one worker is ready).</summary>
        private async Task<bool> IsServerReachableAsync()
        {
            try
            {
                await _translator.GetSupportedLanguagesAsync();
                return true;
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException ||
                                       ex is RetriesExhaustedException || ex is System.Text.Json.JsonException)
            {
                return false;
            }
        }

        private async Task EditSettingsAsync()
        {
            using var form = new ServerSettingsForm(_settings);
            if (form.ShowDialog(this) != DialogResult.OK) return;

            _settings.Save();
            await ConnectAsync();
        }

        /// <summary>Greys out languages the current service does not have (e.g. no model installed on the server).</summary>
        private async Task UpdateLanguageMenuAsync()
        {
            IReadOnlyCollection<string> supported = null;
            try
            {
                supported = await _translator.GetSupportedLanguagesAsync();
            }
            catch (Exception)
            {
                // Server unreachable: leave all enabled; the error is shown when translating
            }

            foreach (ToolStripMenuItem item in _translateMenu.DropDownItems)
            {
                var code = (string)item.Tag;
                item.Enabled = supported == null || supported.Contains(code, StringComparer.OrdinalIgnoreCase);
                item.ToolTipText = item.Enabled ? null : "Not installed on the " + _translator.Name + " server";
            }
        }

        // ------------------------------------------------------------------ actions

        private async Task SetLanguageAsync(string lang)
        {
            _targetLanguage = Languages.Normalize(lang);
            UpdateTitle();
            await RefreshAsync();
        }

        private void SetShowOriginal(bool showOriginal)
        {
            _showOriginal = showOriginal;
            UpdateTitle();

            // Switch the text in every row without translating again
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is RowItem item)
                    row.Cells["Text"].Value = _showOriginal ? item.Entry.Text : item.DisplayText;
            }
        }

        /// <summary>Translates all items (batched, cached) and rebuilds the table.</summary>
        private async Task RefreshAsync()
        {
            var lang = _targetLanguage;
            var entries = _store.GetAll();

            var results = await RunBusyAsync(ct => _translator.TranslateManyAsync(
                entries.Select(e => e.Text).ToList(), lang, ProgressReporter(), ct));

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            var rows = new List<DataGridViewRow>(entries.Count);
            for (var i = entries.Count - 1; i >= 0; i--) // newest first
                rows.Add(CreateRow(new RowItem(entries[i], results[i], lang)));
            _grid.Rows.AddRange(rows.ToArray());
            _grid.ResumeLayout();

            ShowSummary(results);
        }

        private async Task AddAsync()
        {
            if (string.IsNullOrWhiteSpace(_input.Text))
            {
                MessageBox.Show("Please enter some text.");
                return;
            }

            var entry = _store.Add(_input.Text);
            var result = await RunBusyAsync(ct => _translator.TranslateAsync(entry.Text, _targetLanguage, ct));

            _grid.Rows.Insert(0, CreateRow(new RowItem(entry, result, _targetLanguage)));
            _grid.ClearSelection();
            _grid.Rows[0].Selected = true;
            _grid.FirstDisplayedScrollingRowIndex = 0;
            _input.Clear();
            _input.Focus();
            ShowSummary(new[] { result });
        }

        private async Task ImportAsync()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Import texts to translate",
                Filter = "Text or CSV files (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            List<string> texts;
            try
            {
                texts = CsvFile.ReadTexts(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read the file: " + ex.Message);
                return;
            }

            if (texts.Count == 0)
            {
                MessageBox.Show("No texts found in the file.");
                return;
            }

            _store.AddRange(texts);
            _statusLabel.Text = $"Imported {texts.Count:N0} item(s). Translating...";
            await RefreshAsync();
        }

        private void Export()
        {
            if (_grid.Rows.Count == 0)
            {
                MessageBox.Show("Nothing to export.");
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "Export translations",
                Filter = "CSV file (*.csv)|*.csv",
                FileName = "translations-" + _targetLanguage + ".csv"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var rows = _grid.Rows.Cast<DataGridViewRow>()
                .Select(r => r.Tag as RowItem)
                .Where(i => i != null)
                .Select(i => new[]
                {
                    i.Entry.Kind.ToString(),
                    i.Entry.Text,
                    i.Result.DetectedLanguageName,
                    Languages.GetName(i.TargetLanguage),
                    i.Result.Success ? i.Result.TranslatedText : "",
                    i.Result.Success ? "OK" : i.Result.Error
                });

            try
            {
                CsvFile.Write(dialog.FileName,
                    new[] { "Type", "Original", "Detected Language", "Target Language", "Translation", "Status" }, rows);
                _statusLabel.Text = "Exported to " + dialog.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the file: " + ex.Message);
            }
        }

        private void ClearAll()
        {
            if (MessageBox.Show("Remove all items from the list?", "Translator", MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;
            _store.Clear();
            _grid.Rows.Clear();
            _statusLabel.Text = "All items removed.";
        }

        private void ClearCache()
        {
            if (MessageBox.Show("Delete all saved translations? They will be fetched again next time.",
                    "Translator", MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;
            _translator.ClearCache();
            _statusLabel.Text = "Translation cache cleared.";
        }

        private void RemoveSelected()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var row = _grid.SelectedRows[0];
            if (row.Tag is RowItem item && _store.Remove(item.Entry.Id))
                _grid.Rows.Remove(row);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Runs a translation with the UI locked, progress shown and Cancel enabled.</summary>
        private async Task<T> RunBusyAsync<T>(Func<CancellationToken, Task<T>> work)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                return await work(_cts.Token);
            }
            finally
            {
                SetBusy(false);
                _cts.Dispose();
                _cts = null;
            }
        }

        private IProgress<TranslationProgress> ProgressReporter() => new Progress<TranslationProgress>(p =>
        {
            if (p.Total > 0)
            {
                _progressBar.Maximum = p.Total;
                _progressBar.Value = Math.Min(p.Done, p.Total);
            }
            _statusLabel.Text = $"{p.Message} {p.Done:N0} / {p.Total:N0}";
        });

        private void ShowSummary(IReadOnlyCollection<TranslationResult> results)
        {
            var failed = results.Count(r => !r.Success);
            var cancelled = results.Count(r => r.Error == "Cancelled");
            var firstError = results.FirstOrDefault(r => !r.Success && r.Error != "Cancelled")?.Error;

            _statusLabel.Text =
                cancelled > 0 ? $"Cancelled. {results.Count - failed:N0} of {results.Count:N0} translated (the rest can be translated later)." :
                failed > 0 ? $"{failed:N0} of {results.Count:N0} not translated: {firstError}" :
                $"{results.Count:N0} item(s) translated to {Languages.GetName(_targetLanguage)}.";
        }

        /// <summary>
        /// Busy state is shown with the progress bar, status text and the Cancel button, not with a
        /// wait cursor: WinForms only resets UseWaitCursor when the mouse moves, which left the cursor
        /// spinning after translation finished, and the window stays usable (Cancel) while busy.
        /// </summary>
        private void SetBusy(bool busy)
        {
            if (!busy) Cursor.Current = Cursors.Default;
            _menu.Enabled = !busy;
            _addButton.Enabled = !busy;
            _removeButton.Enabled = !busy;
            _cancelButton.Enabled = busy;
            _progressBar.Visible = busy;
            if (busy) _progressBar.Value = 0;
        }

        private DataGridViewRow CreateRow(RowItem item)
        {
            var row = new DataGridViewRow();
            row.CreateCells(_grid, item.Entry.Kind.ToString(), _showOriginal ? item.Entry.Text : item.DisplayText);
            row.Tag = item;
            return row;
        }

        // Tooltip: original text + "from language -> to language"
        private void Grid_CellMouseEnter(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is RowItem item)
                _toolTip.SetToolTip(_grid, item.ToolTipText());
            else
                _toolTip.SetToolTip(_grid, null);
        }

        /// <summary>One row of the table.</summary>
        private class RowItem
        {
            public RowItem(TextEntry entry, TranslationResult result, string targetLanguage)
            {
                Entry = entry;
                Result = result;
                TargetLanguage = targetLanguage;
            }

            public TextEntry Entry { get; }
            public TranslationResult Result { get; }
            public string TargetLanguage { get; }

            public string DisplayText =>
                Result.Success ? Result.TranslatedText : "[Not translated] " + Entry.Text;

            public string ToolTipText()
            {
                var sb = new StringBuilder();
                sb.AppendLine("Original (" + Entry.Kind + "):");
                sb.AppendLine(Wrap(Truncate(Entry.Text, 1500), 80));
                sb.AppendLine();
                sb.Append("From: " + Result.DetectedLanguageName + "  ->  To: " + Languages.GetName(TargetLanguage));

                if (Result.DetectionCorrected)
                    sb.AppendLine().Append("(Language verified by the app - the first detection was wrong)");
                else if (Result.DetectionUncertain)
                    sb.AppendLine().Append("(Language detection is uncertain)");

                if (!Result.Success)
                    sb.AppendLine().Append(Result.Error);
                else if (Languages.SameLanguage(Result.DetectedLanguageCode, TargetLanguage))
                    sb.AppendLine().Append("(Text is already in this language)");

                return sb.ToString();
            }

            private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

            // ToolTip does not wrap long text, so break it into lines
            private static string Wrap(string text, int width)
            {
                var sb = new StringBuilder();
                var lineLength = 0;
                foreach (var word in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (lineLength > 0 && lineLength + word.Length + 1 > width)
                    {
                        sb.AppendLine();
                        lineLength = 0;
                    }
                    else if (lineLength > 0)
                    {
                        sb.Append(' ');
                        lineLength++;
                    }
                    sb.Append(word);
                    lineLength += word.Length;
                }
                return sb.ToString();
            }
        }
    }
}
