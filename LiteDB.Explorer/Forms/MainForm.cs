﻿using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteDB.Explorer
{
    public partial class MainForm : Form
    {
        private readonly SynchronizationContext _synchronizationContext;

        private LiteEngine _db;
        private TaskData _active = null;
        private bool _running = true;

        public MainForm(string filename)
        {
            InitializeComponent();

            // For performance https://stackoverflow.com/questions/4255148/how-to-improve-painting-performance-of-datagridview
            grdResult.DoubleBuffered(true);

            txtFileName.Text = filename ?? "";

            _synchronizationContext = SynchronizationContext.Current;

            if (txtFileName.Text.Length > 0)
            {
                BtnConnect_Click(null, EventArgs.Empty);
            }

            this.FormClosing += (s, e) =>
            {
                // stop all threads
                _running = false;

                foreach(var task in pnlButtons.Controls.Cast<Control>().Select(x => x as Button).Select(x => x.Tag as TaskData))
                {
                    task.Sql = "";
                    task.Thread.Interrupt();
                }
            };
        }

        private void BtnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                if (txtFileName.Enabled)
                {
                    _db = new LiteEngine(txtFileName.Text);
                    btnConnect.Text = "Disconnect";
                    txtFileName.Enabled = false;

                    this.LoadDatabase();
                    BtnAdd_Click(null, null);
                }
                else
                {
                    _db?.Dispose();
                    btnConnect.Text = "Connect";
                    txtFileName.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            var last = pnlButtons.Controls.Cast<Button>().LastOrDefault()?.Tag as TaskData;

            var task = new TaskData()
            {
                Id = ((last?.Id) ?? 0) + 1,
            };

            var btn = new Button() { Text = task.Id.ToString(), Height = 30, Margin = new Padding(0), Tag = task };
            btn.Click += BtnSelect_Click;

            pnlButtons.Controls.Add(btn);

            task.Thread = new Thread(new ThreadStart(() => CreateThread(task)));

            task.Thread.Start();

            BtnSelect_Click(btn, EventArgs.Empty);
        }

        private void BtnSelect_Click(object sender, EventArgs e)
        {
            var btn = sender as Button;

            foreach(var item in pnlButtons.Controls.Cast<Control>().Select(x => x as Button))
            {
                var data = item.Tag as TaskData;
                item.Text = data.Id.ToString();
            }

            if (_active != null)
            {
                _active.Sql = txtSql.Text;
            }

            _active = btn.Tag as TaskData;

            btn.Text = "[" + _active.Id + "]";
            txtSql.Text = _active.Sql;
            btnRun.Enabled = !_active.Running;

            this.LoadResult(_active);
        }

        private void BtnRun_Click(object sender, EventArgs e)
        {
            _active.Sql = txtSql.SelectedText.Length > 0 ? txtSql.SelectedText : txtSql.Text;
            _active.Thread.Interrupt();
        }

        private void LoadDatabase()
        {
            tvwCols.Nodes.Clear();

            var root = tvwCols.Nodes.Add(Path.GetFileNameWithoutExtension(_db.Settings.Filename));
            var system = root.Nodes.Add("System");

            foreach (var key in _db.GetVirtualCollections())
            {
                var col = system.Nodes.Add(key);
                col.Tag = $"SELECT $ FROM {key}";
            }

            root.ExpandAll();
            system.Toggle();

            foreach (var key in _db.GetCollectionNames())
            {
                var col = root.Nodes.Add(key);
                col.Tag = $"SELECT $ FROM {key}";
            }
        }

        private void CreateThread(TaskData task)
        {
            while(_running)
            {
                try
                {
                    Thread.Sleep(Timeout.Infinite);
                }
                catch (ThreadInterruptedException)
                {
                }

                if (task.Sql.Trim() == "") continue;

                var sw = new Stopwatch();
                sw.Start();

                try
                {
                    task.Running = true;

                    _synchronizationContext.Post(new SendOrPostCallback(o =>
                    {
                        btnRun.Enabled = false;
                        grdResult.Clear();
                        txtResult.Clear();
                        lblResultCount.Visible = false;
                        lblElapsed.Text = "Running...";
                    }), task);

                    var reader = _db.Execute(task.Sql);

                    task.Result = reader.Take(UIExtensions.LIMIT + 1).ToList();
                    task.Elapsed = sw.Elapsed;
                    task.Exception = null;
                    task.Running = false;

                    // update form button selected
                    if (_active.Id == task.Id)
                    {
                        _synchronizationContext.Post(new SendOrPostCallback(o =>
                        {
                            this.LoadResult(o as TaskData);
                        }), task);
                    }
                }
                catch (Exception ex)
                {
                    task.Running = false;
                    task.Result = null;
                    task.Elapsed = sw.Elapsed;
                    task.Exception = ex;

                    if (_active.Id == task.Id)
                    {
                        _synchronizationContext.Post(new SendOrPostCallback(o =>
                        {
                            tabResult.SelectedTab = tabText;
                            this.LoadResult(o as TaskData);
                        }), task);
                    }
                }
            }
        }

        private void LoadResult(TaskData data)
        {
            if (data.Exception != null)
            {
                txtResult.BindErrorMessage(data.Exception);
                grdResult.Clear();
            }
            else
            {
                if (tabResult.SelectedTab.Text == "Grid")
                {
                    grdResult.BindBsonData(data.Result);
                    txtResult.Text = "";
                }
                else
                {
                    txtResult.BindBsonData(data.Result);
                    grdResult.Clear();
                }
            }

            if (data.Running)
            {
                lblResultCount.Visible = false;
                lblElapsed.Text = "Running";
            }
            else
            {
                lblResultCount.Text = data.Result?.Count > 0 ? data.Result?.Count + " document(s)" : "no result";
                lblResultCount.Visible = true;
                lblElapsed.Text = data.Elapsed.ToString();
            }

            btnRun.Enabled = !data.Running;
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5 && btnRun.Enabled)
            {
                BtnRun_Click(btnRun, EventArgs.Empty);
            }
        }

        private void TvwCols_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node.Tag is string cmd)
            {
                txtSql.Text = cmd;
            }
        }

        private void TabResult_Selected(object sender, TabControlEventArgs e)
        {
            this.LoadResult(_active);

            if (tabResult.SelectedTab == tabText) ActiveControl = txtResult;
            else if (tabResult.SelectedTab == tabGrid) ActiveControl =  grdResult;
        }

        private void GrdResult_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            var cell = grdResult.Rows[e.RowIndex].Cells[e.ColumnIndex];

            cell.Value = JsonSerializer.Serialize(cell.Tag as BsonValue);
        }

        private void GrdResult_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            var cell = grdResult.Rows[e.RowIndex].Cells[e.ColumnIndex];

            cell.SetBsonValue(cell.Tag as BsonValue);
        }

        private void GrdResult_RowPostPaint(object sender, DataGridViewRowPostPaintEventArgs e)
        {
            var grid = sender as DataGridView;
            var rowIdx = (e.RowIndex + 1).ToString();

            var centerFormat = new StringFormat()
            {
                // right alignment might actually make more sense for numbers
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            var headerBounds = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth, e.RowBounds.Height);
            e.Graphics.DrawString(rowIdx, this.Font, SystemBrushes.ControlText, headerBounds, centerFormat);
        }
    }
}
