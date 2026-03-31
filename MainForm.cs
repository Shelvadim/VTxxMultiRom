using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Forms
{
    public class MainForm : Form
    {
        // ── Controls ──────────────────────────────────────────────────────────
        private ListView    _lstGames     = null!;
        private Button      _btnAdd       = null!;
        private Button      _btnRemove    = null!;
        private Button      _btnUp        = null!;
        private Button      _btnDown      = null!;
        private ComboBox    _cmbChip      = null!;
        private ComboBox    _cmbMapper    = null!;
        private ComboBox    _cmbSubmapper = null!;
        private ComboBox    _cmbPinSwap   = null!;
        private TextBox     _txtOutput    = null!;
        private Button      _btnBrowseOut = null!;
        private CheckBox    _chkNes       = null!;
        private CheckBox    _chkChrRam    = null!;
        private Button      _btnBuild     = null!;
        private Button      _btnGenHeader = null!;
        private Label       _lblStatus    = null!;
        private Label       _lblSpace     = null!;
        private Panel       _pnlBar       = null!;
        private RichTextBox _rtbLog       = null!;

        // ── State ─────────────────────────────────────────────────────────────
        private readonly List<NesRom> _games = new List<NesRom>();
        private double _barPct;
        private bool   _barOver;

        // ── Palette ───────────────────────────────────────────────────────────
        private static readonly Color C_BG     = Color.FromArgb(18,  18,  26);
        private static readonly Color C_PANEL  = Color.FromArgb(26,  28,  40);
        private static readonly Color C_CTRL   = Color.FromArgb(34,  36,  52);
        private static readonly Color C_ACCENT = Color.FromArgb(99,  179, 237);
        private static readonly Color C_GREEN  = Color.FromArgb(72,  199, 142);
        private static readonly Color C_RED    = Color.FromArgb(245, 101, 101);
        private static readonly Color C_YELLOW = Color.FromArgb(246, 173, 85);
        private static readonly Color C_TEXT   = Color.FromArgb(237, 242, 247);
        private static readonly Color C_DIM    = Color.FromArgb(160, 174, 192);
        private static readonly Color C_BORDER = Color.FromArgb(55,  60,  80);

        // ─────────────────────────────────────────────────────────────────────
        public MainForm()
        {
            BuildUI();
            UpdateSpace();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  UI CONSTRUCTION
        // ─────────────────────────────────────────────────────────────────────
        private void BuildUI()
        {
            SuspendLayout();

            Text          = "VT03 OneBus NOR Flash Builder  //  T48 Programmer";
            Size          = new Size(1080, 780);
            MinimumSize   = new Size(920, 660);
            BackColor     = C_BG;
            ForeColor     = C_TEXT;
            Font          = new Font("Consolas", 9f);
            StartPosition = FormStartPosition.CenterScreen;

            // ── Header ────────────────────────────────────────────────────────
            var hdr = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = C_PANEL };
            hdr.Paint += (s, e) =>
            {
                using var p = new Pen(C_ACCENT, 2);
                e.Graphics.DrawLine(p, 0, hdr.Height - 1, hdr.Width, hdr.Height - 1);
            };
            hdr.Controls.Add(new Label
            {
                Text      = "VT03 ONEBUS NOR FLASH BUILDER",
                Font      = new Font("Consolas", 15f, FontStyle.Bold),
                ForeColor = C_ACCENT,
                AutoSize  = true,
                Location  = new Point(14, 8)
            });
            hdr.Controls.Add(new Label
            {
                Text      = "NOR parallel flash image generator  ·  T48 / TL866-3G programmer",
                Font      = new Font("Consolas", 8f),
                ForeColor = C_DIM,
                AutoSize  = true,
                Location  = new Point(16, 34)
            });
            Controls.Add(hdr);

            // ── Body ──────────────────────────────────────────────────────────
            var body = new Panel { BackColor = C_BG };
            body.Location = new Point(0, 56);
            body.Anchor   = AnchorStyles.Top | AnchorStyles.Left |
                            AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(body);

            // ── Left panel ────────────────────────────────────────────────────
            const int LEFT_W = 540;
            var left = MakePanel(C_PANEL, new Rectangle(8, 8, LEFT_W, 0));
            left.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            body.Controls.Add(left);

            left.Controls.Add(SectionLabel("ROM FILES", new Point(10, 10)));

            _lstGames = new ListView
            {
                Location      = new Point(10, 30),
                Anchor        = AnchorStyles.Top | AnchorStyles.Left |
                                AnchorStyles.Right | AnchorStyles.Bottom,
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = false,
                BackColor     = C_CTRL,
                ForeColor     = C_TEXT,
                BorderStyle   = BorderStyle.FixedSingle,
                Font          = new Font("Consolas", 8.5f),
                MultiSelect   = true,
                AllowDrop     = true
            };
            _lstGames.Columns.Add("#",      30);
            _lstGames.Columns.Add("Name",  180);
            _lstGames.Columns.Add("Mapper", 80);
            _lstGames.Columns.Add("PRG",    58);
            _lstGames.Columns.Add("CHR",    58);
            _lstGames.Columns.Add("Mirror", 58);
            _lstGames.Columns.Add("Status", 74);
            _lstGames.SelectedIndexChanged += (s, e) => UpdateButtons();
            _lstGames.ItemDrag  += (s, e) =>
            {
                if (e.Item is ListViewItem it) DoDragDrop(it, DragDropEffects.Move);
            };
            _lstGames.DragEnter += (s, e) => e.Effect = DragDropEffects.Move;
            _lstGames.DragDrop  += OnListDrop;
            left.Controls.Add(_lstGames);

            // Buttons row
            _btnAdd    = Btn("+ ADD ROMs", C_GREEN);
            _btnRemove = Btn("REMOVE",     C_RED);
            _btnUp     = Btn("▲ UP",       C_CTRL);
            _btnDown   = Btn("▼ DOWN",     C_CTRL);
            _btnAdd   .Click += OnAdd;
            _btnRemove.Click += OnRemove;
            _btnUp    .Click += OnUp;
            _btnDown  .Click += OnDown;

            // Space bar
            _lblSpace = new Label
            {
                AutoSize  = false,
                Height    = 14,
                Font      = new Font("Consolas", 7.5f),
                ForeColor = C_DIM,
                Text      = "Flash used: —"
            };
            _pnlBar = new Panel { BackColor = C_CTRL, Height = 18 };
            _pnlBar.Paint += (s, e) => PaintBar(e.Graphics, _pnlBar);

            left.Controls.AddRange(new Control[] {
                _btnAdd, _btnRemove, _btnUp, _btnDown, _lblSpace, _pnlBar
            });
            left.Resize += (s, e) => LayoutLeft(left);

            // ── Right panel ───────────────────────────────────────────────────
            var right = MakePanel(C_PANEL, new Rectangle(LEFT_W + 16, 8, 0, 0));
            right.Anchor = AnchorStyles.Top | AnchorStyles.Left |
                           AnchorStyles.Right | AnchorStyles.Bottom;
            body.Controls.Add(right);

            body.Resize += (s, e) =>
            {
                body.Size  = new Size(ClientSize.Width, ClientSize.Height - 56);
                left.Height  = body.ClientSize.Height - 16;
                right.Location = new Point(LEFT_W + 16, 8);
                right.Size     = new Size(body.ClientSize.Width - LEFT_W - 24,
                                          body.ClientSize.Height - 16);
            };

            // Build settings
            right.Controls.Add(SectionLabel("BUILD SETTINGS", new Point(10, 10)));

            int sy = 32;
            right.Controls.Add(Lbl("Chip size:", new Point(10, sy + 2)));
            _cmbChip = new ComboBox
            {
                Location      = new Point(96, sy),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = C_CTRL,
                ForeColor     = C_TEXT,
                FlatStyle     = FlatStyle.Flat,
                Font          = new Font("Consolas", 8.5f)
            };
            _cmbChip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _cmbChip.Items.AddRange(new object[]
            {
                " 2 MB  (16 Mbit)",
                " 4 MB  (32 Mbit)",
                " 8 MB  (64 Mbit)",
                "16 MB (128 Mbit)",
                "32 MB (256 Mbit)"
            });
            _cmbChip.SelectedIndex = 2;   // default 8 MB
            _cmbChip.SelectedIndexChanged += (s, e) => UpdateSpace();
            right.Controls.Add(_cmbChip);

            sy += 34;
            right.Controls.Add(SectionLabel("OUTPUT", new Point(10, sy)));
            sy += 20;
            _txtOutput = new TextBox
            {
                Location        = new Point(10, sy),
                BackColor       = C_CTRL,
                ForeColor       = C_TEXT,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = new Font("Consolas", 8.5f),
                PlaceholderText = "Select output .bin path...",
                Anchor          = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _btnBrowseOut = Btn("Browse…", C_ACCENT);
            _btnBrowseOut.ForeColor = C_BG;
            _btnBrowseOut.Anchor    = AnchorStyles.Top | AnchorStyles.Right;
            _btnBrowseOut.Click    += OnBrowseOut;
            right.Controls.AddRange(new Control[] { _txtOutput, _btnBrowseOut });

            sy += 34;
            right.Controls.Add(SectionLabel("NES HEADER", new Point(10, sy)));
            _btnGenHeader = new Button
            {
                Text      = "Generate Header",
                Location  = new Point(140, sy - 2),
                Width     = 130,
                Height    = 20,
                BackColor = C_CTRL,
                ForeColor = C_ACCENT,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Consolas", 8f),
                Cursor    = Cursors.Hand
            };
            _btnGenHeader.FlatAppearance.BorderColor = C_ACCENT;
            _btnGenHeader.FlatAppearance.BorderSize  = 1;
            _btnGenHeader.Click += OnGenerateHeader;
            right.Controls.Add(_btnGenHeader);
            sy += 20;

            // ── Mapper label + dropdown ───────────────────────────────────────
            right.Controls.Add(Lbl("Mapper:", new Point(10, sy + 2)));
            _cmbMapper = new ComboBox
            {
                Location      = new Point(68, sy),
                Width         = 120,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = C_CTRL,
                ForeColor     = C_TEXT,
                FlatStyle     = FlatStyle.Flat,
                Font          = new Font("Consolas", 8.5f)
            };
            _cmbMapper.Items.Add("256  (OneBus / VT03)");
            _cmbMapper.SelectedIndex = 0;
            right.Controls.Add(_cmbMapper);

            // ── Submapper label + dropdown (right of mapper) ──────────────────
            right.Controls.Add(Lbl("Submapper:", new Point(200, sy + 2)));
            _cmbSubmapper = new ComboBox
            {
                Location      = new Point(282, sy),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = C_CTRL,
                ForeColor     = C_TEXT,
                FlatStyle     = FlatStyle.Flat,
                Font          = new Font("Consolas", 8.5f),
                Anchor        = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            foreach (var (num, name) in SubmapperItems)
                _cmbSubmapper.Items.Add(new ComboBoxSubmapperItem(num, name));
            _cmbSubmapper.SelectedIndex = 0;   // default: 0 — Normal
            _cmbSubmapper.SelectedIndexChanged += OnSubmapperChanged;
            right.Controls.Add(_cmbSubmapper);

            sy += 34;
            _chkNes = new CheckBox
            {
                Text      = "Also generate test files (.nes for FCEUX  /  .unf for NintendulatorNRS)",
                Location  = new Point(10, sy),
                AutoSize  = true,
                ForeColor = C_YELLOW,
                BackColor = Color.Transparent,
                Font      = new Font("Consolas", 8.5f),
                Checked   = true,
                Cursor    = Cursors.Hand
            };
            right.Controls.Add(_chkNes);

            sy += 24;
            _chkChrRam = new CheckBox
            {
                Text      = "Include games using CHR-RAM (for consoles with CHR-RAM hardware)",
                Location  = new Point(10, sy),
                AutoSize  = true,
                ForeColor = C_DIM,
                BackColor = Color.Transparent,
                Font      = new Font("Consolas", 8.5f),
                Checked   = false,
                Cursor    = Cursors.Hand
            };
            _chkChrRam.CheckedChanged += (s, e) => { RefreshList(); UpdateSpace(); };
            right.Controls.Add(_chkChrRam);

            sy += 28;
            right.Controls.Add(Lbl("Swap pins:", new Point(10, sy + 2)));
            _cmbPinSwap = new ComboBox
            {
                Location      = new Point(96, sy),
                Width         = 280,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = C_CTRL,
                ForeColor     = C_TEXT,
                FlatStyle     = FlatStyle.Flat,
                Font          = new Font("Consolas", 8.5f)
            };
            _cmbPinSwap.Items.Add("None");
            _cmbPinSwap.Items.Add("D1↔D9, D2↔D10  (physical board swap)");
            _cmbPinSwap.SelectedIndex = 0;
            right.Controls.Add(_cmbPinSwap);

            sy += 34;
            _btnBuild = new Button
            {
                Text      = "⚡  BUILD FLASH IMAGE",
                Location  = new Point(10, sy),
                BackColor = C_ACCENT,
                ForeColor = C_BG,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Consolas", 11f, FontStyle.Bold),
                Cursor    = Cursors.Hand,
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Height    = 38
            };
            _btnBuild.FlatAppearance.BorderSize = 0;
            _btnBuild.Click += OnBuild;
            right.Controls.Add(_btnBuild);

            sy += 46;
            _lblStatus = new Label
            {
                Location  = new Point(10, sy),
                AutoSize  = false,
                Height    = 18,
                ForeColor = C_DIM,
                Font      = new Font("Consolas", 8f),
                Text      = "Ready.",
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            right.Controls.Add(_lblStatus);

            sy += 24;
            right.Controls.Add(SectionLabel("BUILD LOG", new Point(10, sy)));
            sy += 20;
            _rtbLog = new RichTextBox
            {
                Location    = new Point(10, sy),
                BackColor   = C_CTRL,
                ForeColor   = C_DIM,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Consolas", 8f),
                ReadOnly    = true,
                WordWrap    = false,
                ScrollBars  = RichTextBoxScrollBars.Both,
                Anchor      = AnchorStyles.Top | AnchorStyles.Left |
                              AnchorStyles.Right | AnchorStyles.Bottom
            };
            right.Controls.Add(_rtbLog);

            right.Resize += (s, e) =>
                LayoutRight(right, _cmbChip, _cmbMapper, _cmbSubmapper,
                            _txtOutput, _btnBrowseOut,
                            _btnBuild, _lblStatus, _rtbLog);

            // Initial sizing
            body.Size  = new Size(ClientSize.Width, ClientSize.Height - 56);
            left.Size  = new Size(LEFT_W, body.ClientSize.Height - 16);
            right.Size = new Size(body.ClientSize.Width - LEFT_W - 24,
                                  body.ClientSize.Height - 16);
            LayoutLeft(left);
            LayoutRight(right, _cmbChip, _cmbMapper, _cmbSubmapper,
                        _txtOutput, _btnBrowseOut,
                        _btnBuild, _lblStatus, _rtbLog);

            Log("VT03 OneBus NOR Flash Builder ready.", C_DIM);
            Log("Add .nes ROMs, set chip size and output path, then BUILD.", C_DIM);

            ResumeLayout();
            UpdateButtons();
        }

        // ── Layout ────────────────────────────────────────────────────────────
        private void LayoutLeft(Panel left)
        {
            int w  = left.ClientSize.Width;
            int h  = left.ClientSize.Height;
            int bY = h - 88;

            _lstGames.Size     = new Size(w - 20, Math.Max(60, bY - 36));
            _btnAdd   .Location = new Point(10,  bY);
            _btnRemove.Location = new Point(120, bY);
            _btnUp    .Location = new Point(210, bY);
            _btnDown  .Location = new Point(288, bY);
            _btnAdd   .Width   = 102;
            _btnRemove.Width   = 82;
            _btnUp    .Width   = 70;
            _btnDown  .Width   = 82;
            _lblSpace .Location = new Point(10, bY + 34);
            _lblSpace .Width   = w - 20;
            _pnlBar   .Location = new Point(10, bY + 50);
            _pnlBar   .Width   = w - 20;
        }

        private static void LayoutRight(Panel right, ComboBox cmbChip,
            ComboBox cmbMapper, ComboBox cmbSubmapper,
            TextBox txtOut, Button btnOut, Button btnBuild,
            Label lblStatus, RichTextBox log)
        {
            int w  = right.ClientSize.Width;
            int h  = right.ClientSize.Height;
            int rw = Math.Max(10, w - 20);

            cmbChip.Width      = rw - 88;
            // Submapper dropdown stretches to fill remaining space right of its fixed label+mapper
            int smLeft = cmbSubmapper.Left;
            cmbSubmapper.Width = Math.Max(60, rw - smLeft + 10);
            txtOut .Width      = Math.Max(60, rw - 90);
            btnOut .Location   = new Point(w - 92, txtOut.Top);
            btnOut .Width      = 82;
            btnBuild.Width     = rw;
            lblStatus.Width    = rw;

            int logTop = btnBuild.Bottom + 46;
            log.Location = new Point(10, logTop);
            log.Size     = new Size(rw, Math.Max(60, h - logTop - 10));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVENT HANDLERS
        // ─────────────────────────────────────────────────────────────────────
        private void OnAdd(object? s, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title       = "Select NES ROM files",
                Filter      = "NES ROMs (*.nes)|*.nes|All files (*.*)|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            int added = 0, bad = 0;
            foreach (string path in dlg.FileNames)
            {
                var rom = NesRom.Load(path);
                if (!rom.IsValid)
                {
                    Log($"✗ {Path.GetFileName(path)}: {rom.ParseError}", C_RED);
                    bad++; continue;
                }
                string? compatWarn = rom.Vt03CompatWarning;
                if (compatWarn != null)
                {
                    // CHR-RAM MMC3 games will grey screen unless the console has CHR-RAM hardware
                    if (rom.HasChrRam)
                    {
                        bool allowChrRam = _chkChrRam?.Checked ?? false;
                        if (!allowChrRam)
                        {
                            Log($"✗ {rom.FileName}  [{rom.MapperDescription}]  ⚠ {compatWarn}  (enable 'Include CHR-RAM games' to add)", C_RED);
                            bad++; continue;
                        }
                        Log($"⚠ {rom.FileName}  [{rom.MapperDescription}]  CHR-RAM — included (ensure your console has CHR-RAM)", C_YELLOW);
                    }
                    else
                    {
                        // Large PRG — warn but allow
                        Log($"⚠ {rom.FileName}  [{rom.MapperDescription}]  PRG:{rom.PrgSize / 1024}KB  CHR:{rom.ChrSize / 1024}KB  — {compatWarn}", C_YELLOW);
                    }
                }
                else
                {
                    Log($"+ {rom.FileName}  [{rom.MapperDescription}]  " +
                        $"PRG:{rom.PrgSize / 1024}KB  " +
                        $"CHR:{(rom.ChrSize > 0 ? rom.ChrSize / 1024 + "KB" : "RAM")}" +
                        (rom.IsSupportedByVT03 ? "" : "  ⚠ mapper not tested on OneBus"),
                        rom.IsSupportedByVT03 ? C_GREEN : C_YELLOW);
                }
                _games.Add(rom);
                added++;
            }
            RefreshList();
            UpdateSpace();
            Status(added > 0
                ? $"Added {added} ROM(s)" + (bad > 0 ? $", {bad} failed" : "")
                : "No ROMs added.");
        }

        private void OnRemove(object? s, EventArgs e)
        {
            foreach (int i in _lstGames.SelectedIndices.Cast<int>()
                                        .OrderByDescending(x => x))
                _games.RemoveAt(i);
            RefreshList();
            UpdateSpace();
        }

        private void OnUp(object? s, EventArgs e)
        {
            if (_lstGames.SelectedIndices.Count != 1) return;
            int i = _lstGames.SelectedIndices[0];
            if (i <= 0) return;
            (_games[i], _games[i - 1]) = (_games[i - 1], _games[i]);
            RefreshList();
            _lstGames.Items[i - 1].Selected = true;
        }

        private void OnDown(object? s, EventArgs e)
        {
            if (_lstGames.SelectedIndices.Count != 1) return;
            int i = _lstGames.SelectedIndices[0];
            if (i >= _games.Count - 1) return;
            (_games[i], _games[i + 1]) = (_games[i + 1], _games[i]);
            RefreshList();
            _lstGames.Items[i + 1].Selected = true;
        }


        private void OnSubmapperChanged(object? s, EventArgs e)
        {
            var item = _cmbSubmapper.SelectedItem as ComboBoxSubmapperItem;
            int sm   = item?.Number ?? 0;
            if (sm >= 11 && sm <= 15)
            {
                Log($"⚠ Submapper {sm}: This console uses hardware CPU opcode bit-swapping.", C_YELLOW);
                Log( "  Standard NES ROMs will NOT work correctly on this hardware.", C_YELLOW);
                Log( "  Only use ROMs that were originally compiled for this console type.", C_YELLOW);
                Log( "  The submapper value is recorded in the NES 2.0 header for emulator use only.", C_DIM);
            }
        }

        private void OnGenerateHeader(object? s, EventArgs e)
        {
            var item      = _cmbSubmapper.SelectedItem as ComboBoxSubmapperItem;
            int submapper = item?.Number ?? 0;
            string? chipStr = _cmbChip.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(chipStr))
                chipStr = "8 MB";
            int chipMb = int.TryParse(chipStr.Split(' ')[0], out var val) ? val : 8;

            string desc = Mapper256Builder.DescribeHeader(submapper, chipMb);
            foreach (string line in desc.Split('\n'))
                Log(line.TrimEnd(), C_ACCENT);
        }

        private void OnBrowseOut(object? s, EventArgs e)
        {
            using var dlg = new SaveFileDialog
            {
                Title      = "Save NOR Flash Image As",
                Filter     = "Binary files (*.bin)|*.bin|All files (*.*)|*.*",
                DefaultExt = "bin",
                FileName   = "vt03_flash.bin"
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                _txtOutput.Text = dlg.FileName;
        }

        private void OnBuild(object? s, EventArgs e)
        {
            var cfg = CurrentConfig();
            if (cfg.Games.Count == 0)
            {
                Status("Add at least one ROM first.", C_RED); return;
            }
            if (string.IsNullOrWhiteSpace(cfg.OutputPath))
            {
                Status("Set an output path first.", C_RED); return;
            }

            _btnBuild.Enabled = false;
            Status("Building…");
            Log("", null);
            Log("── Build started ─────────────────────────────────────────", C_ACCENT);
            var smItem = _cmbSubmapper.SelectedItem as ComboBoxSubmapperItem;
            Log($"Mapper 256  Submapper {smItem?.Number ?? 0}: {smItem?.ToString()?.Trim() ?? "Normal"}", C_DIM);

            bool          ok     = true;
            BuildResult?  result = null;

            try
            {
                // Progress<T> marshals callbacks to the UI thread automatically
                var progress = new Progress<string>(msg =>
                {
                    if (msg.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                        ok = false;
                    Log(msg, null);
                });

                result = RomBuilder.Build(cfg, progress);

                string binPath = cfg.OutputPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                                 ? cfg.OutputPath
                                 : cfg.OutputPath + ".bin";
                File.WriteAllBytes(binPath, result.NorBinary);
                Log($"BIN: {binPath}  ({result.NorBinary.Length / 1024} KB)", null);

                if (cfg.GenerateNes)
                {
                    // .nes for FCEUX (mapper 256 — NROM/CNROM works; use NintendulatorNRS for MMC3)
                    string nesPath = Path.ChangeExtension(binPath, ".nes");
                    File.WriteAllBytes(nesPath, result.NesFile);
                    Log($"NES: {nesPath}  (FCEUX — mapper 256)", null);

                    // .unf for NintendulatorNRS (UNL-OneBus UNIF format)
                    string unfPath = Path.ChangeExtension(binPath, ".unf");
                    File.WriteAllBytes(unfPath, result.UnifFile);
                    Log($"UNF: {unfPath}  (NintendulatorNRS — UNL-OneBus)", null);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                Log($"EXCEPTION: {ex.Message}", C_RED);
            }

            if (!ok)
            {
                Log("── Build FAILED ──────────────────────────────────────────", C_RED);
                Status("✗ Build failed — see log.", C_RED);
                _btnBuild.Enabled = true;
                return;
            }

            Log("── Build complete ────────────────────────────────────────", C_GREEN);
            string binOut = cfg.OutputPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            ? cfg.OutputPath
                            : cfg.OutputPath + ".bin";
            long sz = result?.NorBinary.Length ?? 0;
            Status($"✓ Done — {result?.GameCount ?? 0} games, {sz / 1024} KB", C_GREEN);

            string info = $"Multicart built!\n\n" +
                          $"BIN: {binOut}\n" +
                          $"Size: {sz / 1024} KB  ({result?.GameCount ?? 0} games)";
            if (cfg.GenerateNes)
                info += $"\n\nTest files:\n" +
                        $"  .nes → FCEUX (mapper 256, NROM/CNROM)\n" +
                        $"  .unf → NintendulatorNRS (UNL-OneBus, all mappers)\n" +
                        $"  Note: for MMC3 games use NintendulatorNRS (.unf)";
            info += "\n\nFlash the .bin to your NOR chip using the T48 / Xgpro.";

            MessageBox.Show(info, "Build Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            _btnBuild.Enabled = true;
        }

        private void OnListDrop(object? s, DragEventArgs e)
        {
            if (e.Data?.GetData(typeof(ListViewItem)) is not ListViewItem drag) return;
            var pt  = _lstGames.PointToClient(new Point(e.X, e.Y));
            var tgt = _lstGames.GetItemAt(pt.X, pt.Y);
            if (tgt == null || tgt == drag) return;
            int from = drag.Index, to = tgt.Index;
            var rom = _games[from];
            _games.RemoveAt(from);
            _games.Insert(to, rom);
            RefreshList();
            _lstGames.Items[to].Selected = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────────────
        private BuildConfig CurrentConfig() => new BuildConfig
        {
            Games        = new List<NesRom>(_games),
            OutputPath   = _txtOutput?.Text.Trim() ?? string.Empty,
            GenerateNes  = _chkNes?.Checked ?? true,
            AllowChrRam  = _chkChrRam?.Checked ?? false,
            PinSwap      = _cmbPinSwap?.SelectedIndex ?? 0,
            Mapper       = 256,   // only one mapper for now
            Submapper    = (_cmbSubmapper?.SelectedItem as ComboBoxSubmapperItem)?.Number ?? 0,
            ChipSizeMb   = _cmbChip?.SelectedIndex switch
            {
                0 => 2,
                1 => 4,
                2 => 8,
                3 => 16,
                4 => 32,
                _ => 8
            }
        };

        private void RefreshList()
        {
            _lstGames.BeginUpdate();
            _lstGames.Items.Clear();
            for (int i = 0; i < _games.Count; i++)
            {
                var g    = _games[i];
                var item = new ListViewItem((i + 1).ToString("D2"));
                item.SubItems.Add(g.DisplayName);
                item.SubItems.Add(g.MapperDescription);
                item.SubItems.Add(g.PrgSize > 0 ? $"{g.PrgSize / 1024}KB" : "—");
                item.SubItems.Add(g.ChrSize > 0 ? $"{g.ChrSize / 1024}KB" : "RAM");
                item.SubItems.Add(g.Mirroring.ToString().Substring(0, 1));
                string status;
                Color rowColor;
                if (!g.IsValid)                     { status = "✗ Bad";     rowColor = C_RED; }
                else if (g.HasChrRam && g.Mapper==4)
                {
                    bool allowChrRam = _chkChrRam?.Checked ?? false;
                    status   = allowChrRam ? "⚠ CHR-RAM" : "✗ CHR-RAM";
                    rowColor = allowChrRam ? C_YELLOW    : C_RED;
                }
                else if (g.Vt03CompatWarning != null){ status = "⚠ IRQ?";   rowColor = C_YELLOW; }
                else if (!g.IsSupportedByVT03)      { status = "⚠ Mapper";  rowColor = C_YELLOW; }
                else                                { status = "OK";         rowColor = C_TEXT; }
                item.ForeColor = rowColor;
                item.SubItems.Add(status);
                _lstGames.Items.Add(item);
            }
            _lstGames.EndUpdate();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool any = _lstGames.SelectedIndices.Count > 0;
            bool one = _lstGames.SelectedIndices.Count == 1;
            _btnRemove.Enabled = any;
            _btnUp    .Enabled = one && _lstGames.SelectedIndices[0] > 0;
            _btnDown  .Enabled = one && _lstGames.SelectedIndices[0] < _games.Count - 1;
        }

        private void UpdateSpace()
        {
            if (_lblSpace == null || _pnlBar == null) return;
            var info = SpaceCalculator.Calculate(CurrentConfig());
            _barPct  = info.Percent;
            _barOver = info.Overflow;
            _lblSpace.Text      = $"Flash used: {info.Summary}";
            _lblSpace.ForeColor = info.Overflow ? C_RED : C_DIM;
            _pnlBar.Invalidate();
        }

        private void PaintBar(Graphics g, Panel p)
        {
            Color fill = _barOver ? C_RED : C_GREEN;
            g.Clear(C_CTRL);
            using var pen = new Pen(C_BORDER);
            g.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            int fw = (int)((p.Width - 2) * Math.Min(_barPct / 100.0, 1.0));
            if (fw > 0)
            {
                using var br = new SolidBrush(Color.FromArgb(180, fill));
                g.FillRectangle(br, 1, 1, fw, p.Height - 2);
            }
            string pct = $"{_barPct:F0}%";
            using var tf = new Font("Consolas", 7.5f, FontStyle.Bold);
            using var tb = new SolidBrush(C_TEXT);
            var sz = g.MeasureString(pct, tf);
            g.DrawString(pct, tf, tb,
                         (p.Width  - sz.Width)  / 2,
                         (p.Height - sz.Height) / 2);
        }

        private void Log(string msg, Color? col = null)
        {
            _rtbLog.SelectionStart  = _rtbLog.TextLength;
            _rtbLog.SelectionLength = 0;
            _rtbLog.SelectionColor  = col ?? C_DIM;
            _rtbLog.AppendText(msg + "\n");
            _rtbLog.ScrollToCaret();
        }

        private void Status(string msg, Color? col = null)
        {
            _lblStatus.Text      = msg;
            _lblStatus.ForeColor = col ?? C_DIM;
        }

        // ── Widget factories ──────────────────────────────────────────────────
        private static Panel MakePanel(Color bg, Rectangle r) => new Panel
            { BackColor = bg, Location = r.Location, Size = r.Size };

        private static Label SectionLabel(string t, Point loc) => new Label
        {
            Text      = t,
            Location  = loc,
            AutoSize  = true,
            ForeColor = C_ACCENT,
            Font      = new Font("Consolas", 8f, FontStyle.Bold)
        };

        private static Label Lbl(string t, Point loc) => new Label
        {
            Text      = t,
            Location  = loc,
            AutoSize  = true,
            ForeColor = C_DIM,
            Font      = new Font("Consolas", 8.5f)
        };

        private static Button Btn(string t, Color bg) => new Button
        {
            Text      = t,
            Size      = new Size(100, 26),
            BackColor = bg,
            ForeColor = C_TEXT,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Consolas", 8f, FontStyle.Bold),
            Cursor    = Cursors.Hand
        };

        // ── Submapper table (NESdev mapper 256 submapper reference) ───────────
        //  Number → display name shown in the dropdown
        private static readonly (int Number, string Name)[] SubmapperItems =
        {
            ( 0, " 0  Normal"),
            ( 1, " 1  Waixing VT03"),
            ( 2, " 2  Power Joy Supermax"),
            ( 3, " 3  Zechess / Hummer Team"),
            ( 4, " 4  Sports Game 69-in-1"),
            ( 5, " 5  Waixing VT02"),
            (11, "11  Vibes"),
            (12, "12  Cheertone"),
            (13, "13  Cube Tech"),
            (14, "14  Karaoto"),
            (15, "15  Jungletac"),
        };
    }

    // ── Helper: ComboBox item that carries the submapper number ───────────────
    internal sealed class ComboBoxSubmapperItem
    {
        public int    Number { get; }
        private string _label;
        public ComboBoxSubmapperItem(int number, string label)
        { Number = number; _label = label; }
        public override string ToString() => _label;
    }
}
