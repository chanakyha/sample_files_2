using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Translator.Models;
using Translator.Services;

namespace Translator
{
    public class MainForm : Form
    {
        private readonly EntryStore _store = new EntryStore();
        private readonly ITranslationService _translator = new GoogleTranslationService();

        private string _targetLanguage = Languages.Default;
        private bool _showOriginal;

        private ToolStripMenuItem _showMenu;
        private ToolStripMenuItem _showTranslatedItem;
        private ToolStripMenuItem _showOriginalItem;

        private MenuStrip _menu;
        private ToolStripMenuItem _translateMenu;
        private DataGridView _grid;
        private TextBox _input;
        private Button _addButton;
        private Button _removeButton;
        private ToolTip _toolTip;

        public MainForm()
        {
            BuildUi();
            Load += async (s, e) => await RefreshAsync();
        }

        private void BuildUi()
        {
            Text = "Translator";
            Width = 900;
            Height = 600;

            // Nav: "Translate" menu that opens on hover and lists all 24 languages
            _menu = new MenuStrip();
            _translateMenu = new ToolStripMenuItem("Translate");
            foreach (var lang in Languages.Targets)
            {
                var item = new ToolStripMenuItem(lang.Value) { Tag = lang.Key };
                item.Click += async (s, e) => await SetLanguageAsync((string)((ToolStripItem)s).Tag);
                _translateMenu.DropDownItems.Add(item);
            }
            _translateMenu.MouseHover += (s, e) => _translateMenu.ShowDropDown();
            _menu.Items.Add(_translateMenu);

            var checkItem = new ToolStripMenuItem("Check Connection");
            checkItem.Click += (s, e) =>
            {
                using var form = new ConnectionCheckForm();
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

            // Table of translated words / sentences / paragraphs
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
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
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

            _removeButton = new Button { Text = "Remove", Dock = DockStyle.Bottom };
            _removeButton.Click += (s, e) => RemoveSelected();

            var buttons = new Panel { Dock = DockStyle.Right, Width = 100 };
            buttons.Controls.Add(_addButton);
            buttons.Controls.Add(_removeButton);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 90 };
            bottom.Controls.Add(_input);
            bottom.Controls.Add(buttons);

            Controls.Add(_grid);
            Controls.Add(bottom);
            Controls.Add(_menu);

            UpdateTitle();
        }

        private void UpdateTitle()
        {
            Text = "Translator - " + Languages.GetName(_targetLanguage);
            _grid.Columns["Text"].HeaderText = _showOriginal
                ? "Original Text"
                : "Translated Text (" + Languages.GetName(_targetLanguage) + ")";
            foreach (ToolStripMenuItem item in _translateMenu.DropDownItems)
                item.Checked = (string)item.Tag == _targetLanguage;
            _showTranslatedItem.Checked = !_showOriginal;
            _showOriginalItem.Checked = _showOriginal;
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

        private async Task SetLanguageAsync(string lang)
        {
            _targetLanguage = Languages.Normalize(lang);
            UpdateTitle();
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            var lang = _targetLanguage;
            var entries = _store.GetAll();

            SetBusy(true);
            try
            {
                var results = await Task.WhenAll(entries.Select(e => _translator.TranslateAsync(e.Text, lang)));

                _grid.Rows.Clear();
                for (var i = entries.Count - 1; i >= 0; i--) // newest first
                    AddRow(_grid.Rows.Count, new RowItem(entries[i], results[i], lang));
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task AddAsync()
        {
            if (string.IsNullOrWhiteSpace(_input.Text))
            {
                MessageBox.Show("Please enter some text.");
                return;
            }

            SetBusy(true);
            try
            {
                var entry = _store.Add(_input.Text);
                var result = await _translator.TranslateAsync(entry.Text, _targetLanguage);

                AddRow(0, new RowItem(entry, result, _targetLanguage));
                _grid.ClearSelection();
                _grid.Rows[0].Selected = true;
                _grid.FirstDisplayedScrollingRowIndex = 0;
                _input.Clear();
            }
            finally
            {
                SetBusy(false);
                _input.Focus();
            }
        }

        private void AddRow(int index, RowItem item)
        {
            _grid.Rows.Insert(index, item.Entry.Kind.ToString(), _showOriginal ? item.Entry.Text : item.DisplayText);
            _grid.Rows[index].Tag = item;
        }

        private void RemoveSelected()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var row = _grid.SelectedRows[0];
            if (row.Tag is RowItem item && _store.Remove(item.Entry.Id))
                _grid.Rows.Remove(row);
        }

        private void SetBusy(bool busy)
        {
            UseWaitCursor = busy;
            _menu.Enabled = !busy;
            _addButton.Enabled = !busy;
            _removeButton.Enabled = !busy;
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
                sb.AppendLine(Wrap(Entry.Text, 80));
                sb.AppendLine();
                sb.Append("From: " + Result.DetectedLanguageName + "  ->  To: " + Languages.GetName(TargetLanguage));

                if (Result.DetectionCorrected)
                    sb.AppendLine().Append("(Language verified by the app - Google's first guess was wrong)");
                else if (Result.DetectionUncertain)
                    sb.AppendLine().Append("(Language detection is uncertain)");

                if (!Result.Success)
                    sb.AppendLine().Append(Result.Error);
                else if (Languages.SameLanguage(Result.DetectedLanguageCode, TargetLanguage))
                    sb.AppendLine().Append("(Text is already in this language)");

                return sb.ToString();
            }

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
