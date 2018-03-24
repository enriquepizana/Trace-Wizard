﻿#region License
// Copyright (c) 2016 Timothy Slater
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

//TODO: Fix the code for navigating to a stacktrace, because TreeView is lazyloaded the node may not exist yet.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;
using TraceWizard.Data;
using TraceWizard.Data.Serialization;
using TraceWizard.Processors;
using TraceWizard.UI;

namespace TraceWizard
{
    public partial class MainForm : Form
    {
        public static bool IsRunningMono = false;
        public static bool IsRunningOSX = false;
        private double Version = 1.4;

        private void CheckForNewVersion()
        {

            /*https://github.com/tslater2006/Trace-Wizard/releases/latest*/
            HttpWebRequest req = WebRequest.CreateHttp("https://github.com/tslater2006/Trace-Wizard/releases/latest");
            req.AllowAutoRedirect = false;
            var resp = (HttpWebResponse)req.GetResponse();
            var latestVersion = double.Parse(resp.Headers["Location"].Split('/').Last());
            if (latestVersion > Version)
            {
                var result = MessageBox.Show($"Version {latestVersion} is available for download on GitHub. Would you like to go there now?", "New Version Available",MessageBoxButtons.YesNo);
                if (result == DialogResult.Yes)
                {
                    System.Diagnostics.Process.Start("https://github.com/tslater2006/Trace-Wizard/releases/latest");
                }
            }
        }

        public MainForm()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            InitializeComponent();
            CheckForNewVersion();
            executionTree.MouseDown += (sender, args) => 
                executionTree.SelectedNode = executionTree.GetNodeAt(args.X, args.Y);

            IsRunningMono = Type.GetType("Mono.Runtime") != null;
            
            if (IsRunningMono && Properties.Settings.Default.MonoFirstRun)
            {
                var result = new MonoWarningDialog().ShowDialog(this);
                if (result == DialogResult.OK)
                {
                    Properties.Settings.Default.MonoFirstRun = false;
                    Properties.Settings.Default.Save();
                }
            }

            if (IsRunningMono)
            {
                /* detect if running OSX for special "Copy" functionality */
            if (Directory.Exists("/Applications")
                    && Directory.Exists("/Users")) {
                    IsRunningOSX = true;
                }
            }

        }

        public MainForm(bool shouldOpenFile) : this()
        {
            if (shouldOpenFile)
            {
                openToolStripMenuItem_Click(openToolStripMenuItem, new EventArgs());
            }
        }

        int previousSortColumn = 0;
        bool sortAscending = true;
        TraceData traceData;
        Stopwatch sw = new Stopwatch();
        SQLDisplayType currentSQLDisplay;
        TraceProcessor processor = null;

        Dictionary<SQLStatement, TreeNode> SQLMapToTree = new Dictionary<SQLStatement, TreeNode>();
        Dictionary<ExecutionCall, TreeNode> ExecCallToTree = new Dictionary<ExecutionCall, TreeNode>();

        Form _currentModal;

        private bool _findBoxVisible = false;
        private bool _execSearchInProgress = false;
        private bool _sqlSearchInProgress = false;
        private bool _traceSearchInProgress = false;


        private void Form1_Load(object sender, EventArgs e)
        {
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Value = 0;

            var cur = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;

            memoryUsage.Text = String.Format("Cur: {0}MB", (int)(cur / 1024 / 1024));
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new AboutBox().ShowDialog(this);
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.Filter = "Trace Files|*.tracesql;*.trc;*.twiz";
            openFileDialog1.FileName = "";
            var result = openFileDialog1.ShowDialog();

            executionTree.Nodes.Clear();
            sqlListView.Items.Clear();
            stackTraceListView.Items.Clear();
            detailsBox.Items.Clear();

            if (result == DialogResult.OK)
            {
                string filename = openFileDialog1.FileName;
                // Process the file

                if (TraceDeserializer.IsSerializedData(filename))
                {
                    if (TraceDeserializer.IsSerializedVersionSupported(filename))
                    {
                        sw.Reset();
                        sw.Start();
                        traceData = new TraceDeserializer().DeserializeTraceData(new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.None));
                        UpdateUI();
                        sw.Stop();

                        MessageBox.Show("Trace data loaded in " + sw.Elapsed.TotalSeconds + " seconds.");
                        sw.Reset();
                    }
                    else
                    {
                        MessageBox.Show("This version of Trace Wizard cannot import the serialized data, maybe it was serialized with an older version.");
                    }
                }
                else
                {
                    processor = new TraceProcessor(filename);
                    processor.WorkerReportsProgress = true;
                    processor.WorkerSupportsCancellation = true;

                    processor.ProgressChanged += Processor_ProgressChanged;
                    processor.RunWorkerCompleted += Processor_RunWorkerCompleted;
                    sw.Reset();
                    sw.Start();
                    processor.RunWorkerAsync();
                }
            }
        }

        private void Processor_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            progressBar.Value = 0;
            if (e.Cancelled)
            {
                MessageBox.Show("Trace file processing cancelled.");
                processor = null;
                System.GC.Collect();
                return;
            }

            traceData = (TraceData)e.Result;
            UpdateUI();
            sw.Stop();

            MessageBox.Show("Trace file processed in " + sw.Elapsed.TotalSeconds + " seconds.");
            sw.Reset();
            System.GC.Collect();
        }

        private void UpdateUI()
        {
            _execSearchInProgress = false;
            if (traceData == null)
            {
                return;
            }
            UIBuilder.BuildExecutionTree(traceData, executionTree, SQLMapToTree, ExecCallToTree);
            sortAscending = true; 

            switch(currentSQLDisplay)
            {
                case SQLDisplayType.ALL:
                    if (errorsToolStripMenuItem.Checked)
                    {
                        UIBuilder.BuildAllSQLList(sqlListView, traceData.SQLStatements.Where(s => s.IsError).ToList());
                    } else
                    {
                        UIBuilder.BuildAllSQLList(sqlListView, traceData.SQLStatements);
                    }
                    break;
                case SQLDisplayType.WHERE:
                    UIBuilder.BuildWhereSQLList(sqlListView, traceData.SQLByWhere);
                    break;
                case SQLDisplayType.FROM:
                    UIBuilder.BuildFromSQLList(sqlListView, traceData.SQLByFrom);
                    break;
            }
            
            UIBuilder.BuildStackTraceList(stackTraceListView, traceData.StackTraces);

            var tabPage = UIBuilder.BuildStatisticsPage(traceData.Statistics, handleStatisticDoubleClick);
            StatsTab.Controls.Clear();

            StatsTab.Controls.Add(tabPage);

            previousSortColumn = 0;

        }



        private void Processor_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            progressBar.Value = e.ProgressPercentage;
        }

        private void timer1_Tick(object sender, EventArgs e)
        {

            var cur = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
            memoryUsage.Text = String.Format("Cur: {0}MB", (int)(cur / 1024 / 1024));
        }

        private void listView1_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            /* get column name */

            if (previousSortColumn == e.Column)
            {
                sortAscending = !sortAscending;
            }
            else
            {
                sortAscending = true;
            }


            sqlListView.ListViewItemSorter = new ListViewItemComparer(e.Column, sortAscending);
            previousSortColumn = e.Column;
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (progressBar.GetCurrentParent() != null)
            {
                progressBar.Width = progressBar.GetCurrentParent().Width - 120;
            }
        }

        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (((ListView)sender).SelectedItems.Count > 0)
            {
                var selectedIndex = ((ListView)sender).SelectedItems[0];

                ItemExplainer.UpdateExplanation(detailsBox, selectedIndex.Tag);
            }

        }

        private void executionTree_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (e.Node?.Tag != null)
                {
                    ItemExplainer.UpdateExplanation(detailsBox, e.Node.Tag);
                }
            }
            else
            {
                executionContextStrip.Show(sender as Control, e.Location);
            }
        }

        private void listView1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (sqlListView.SelectedItems.Count > 0)
            {
                var selectedItem = sqlListView.SelectedItems[0];
                if (selectedItem.Tag != null)
                {
                    if (selectedItem.Tag.GetType().Equals(typeof(SQLByFrom)))
                    {
                        var form = new SQLViewer(this, traceData.SQLStatements, (SQLByFrom)selectedItem.Tag);
                        _currentModal = form;
                        form.ShowDialog(this);
                    }
                    if (selectedItem.Tag.GetType().Equals(typeof(SQLByWhere)))
                    {
                        var form = new SQLViewer(this, traceData.SQLStatements, (SQLByWhere)selectedItem.Tag);
                        _currentModal = form;
                        form.ShowDialog(this);
                    }

                    if (selectedItem.Tag.GetType().Equals(typeof(SQLStatement)))
                    {
                        if (((SQLStatement)(selectedItem.Tag)).Cobol)
                        {
                            return;
                        }
                        if (IsRunningMono)
                        {
                            var callDepth = 0;
                            var sqlStatement = selectedItem.Tag as SQLStatement;
                            var parent = sqlStatement.ParentCall;
                            while (parent != null)
                            {
                                callDepth++;
                                parent = parent.Parent;
                            }
                            if (callDepth > 10)
                            {
                                var result = MessageBox.Show("Due to poor performance of TreeView in Mono, this may take some time (depending on how nested the SQL statement is), would you like to continue?.", "Long operation", MessageBoxButtons.YesNo);
                                if (result == DialogResult.No)
                                {
                                    return;
                                }
                            }
                        }
                        GoToSQLStatementInExecPath((SQLStatement)(selectedItem.Tag));
                    }
                }
            }

        }

        public void GoToSQLStatementInExecPath(SQLStatement statement)
        {
            /* switch tabs */
            mainTabStrip.SelectedTab = executionPathTab;

            foreach (TreeNode node in executionTree.Nodes)
            {
                node.Collapse();
            }
            if (SQLMapToTree.ContainsKey(statement))
            {
                var nodeParent = SQLMapToTree[statement];
                while (nodeParent != null)
                {
                    nodeParent.Expand();
                    nodeParent = nodeParent.Parent;
                }
            }
            else
            {
                // the node doesn't exist on the tree yet, we need to build it out
                var parent = statement.ParentCall;

                BuildOutTreeForCall(parent);

                /* parent should exist on the tree, start building down */
            }

            if (_currentModal != null)
            {
                _currentModal.Hide();
            }
            executionTree.SelectedNode = SQLMapToTree[statement];
            executionTree.Focus();
        }

        private void BuildOutTreeForCall(ExecutionCall call)
        {
            Stack<ExecutionCall> parentCalls = new Stack<ExecutionCall>();
            var parent = call;
            while (parent != null && ExecCallToTree.ContainsKey(parent) == false)
            {
                parentCalls.Push(parent);
                parent = parent.Parent;
            }

            /* we need to "expand" this parent to populate it's children */
            var current = parent;
            while (parentCalls.Count > 0)
            {
                BuildExpandingNode(ExecCallToTree[current], current);
                ExecCallToTree[current].Parent.Expand();

                current = parentCalls.Pop();
            }

            BuildExpandingNode(ExecCallToTree[current], current);
            ExecCallToTree[current].Parent.Expand();
            ExecCallToTree[current].Expand();
        }

        public void GoToStackTraceInExecPath(StackTraceEntry trace)
        {
            /* switch tabs */
            mainTabStrip.SelectedTab = executionPathTab;

            foreach (TreeNode node in executionTree.Nodes)
            {
                node.Collapse();
            }
            if (trace.Parent == null)
            {
                /* try to find the closest one */
                var bestGuess = traceData.AllExecutionCalls.Where(c => c.StartLine <= trace.LineNumber && c.StopLine >= trace.LineNumber).Last();
                trace.Parent = bestGuess;
            }
            if (ExecCallToTree.ContainsKey(trace.Parent))
            {
                var nodeParent = ExecCallToTree[trace.Parent];
                while (nodeParent != null)
                {
                    nodeParent.Expand();
                    nodeParent = nodeParent.Parent;
                }
            }
            else
            {
                // the node doesn't exist on the tree yet, we need to build it out
                var parent = trace.Parent;

                BuildOutTreeForCall(parent);

                /* parent should exist on the tree, start building down */
            }

            if (_currentModal != null)
            {
                _currentModal.Hide();
            }
            executionTree.SelectedNode = ExecCallToTree[trace.Parent];
            executionTree.Focus();

        }

        private void stackTraceListView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (stackTraceListView.SelectedItems.Count > 0)
            {
                var selectedItem = stackTraceListView.SelectedItems[0];
                if (IsRunningMono)
                {
                    var callDepth = 0;
                    var sqlStatement = selectedItem.Tag as SQLStatement;
                    var parent = sqlStatement.ParentCall;
                    while (parent != null)
                    {
                        callDepth++;
                        parent = parent.Parent;
                    }
                    if (callDepth > 10)
                    {
                        var result = MessageBox.Show("Due to poor performance of TreeView in Mono, this may take some time (depending on how nested the Stack Trace statement is), would you like to continue?.", "Long operation", MessageBoxButtons.YesNo);
                        if (result == DialogResult.No)
                        {
                            return;
                        }
                    }
                }
                
                if (selectedItem.Tag != null)
                {
                    GoToStackTraceInExecPath((StackTraceEntry)(selectedItem.Tag));
                }
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void mainTabStrip_Selected(object sender, TabControlEventArgs e)
        {
            /* ensure we are closing the find box */
            _findBoxVisible = true;

            if (e.TabPage == executionPathTab)
            {
                processSearchBoxDisplay(executionTree, execFindBox, new KeyEventArgs(Keys.Escape));
                if (_execSearchInProgress)
                {
                    RestoreExecutionNodes();
                    _execSearchInProgress = false;
                }

            }
            else if (e.TabPage == sqlStatementsTab)
            {
                processSearchBoxDisplay(sqlListView, sqlFindBox, new KeyEventArgs(Keys.Escape));

                if (_sqlSearchInProgress)
                {
                    if (currentSQLDisplay == SQLDisplayType.ALL)
                    {
                        UIBuilder.BuildAllSQLList(sqlListView, traceData.SQLStatements);
                    }
                    else if (currentSQLDisplay == SQLDisplayType.WHERE)
                    {
                        UIBuilder.BuildWhereSQLList(sqlListView, traceData.SQLByWhere);
                    }
                    else if (currentSQLDisplay == SQLDisplayType.FROM)
                    {
                        UIBuilder.BuildFromSQLList(sqlListView, traceData.SQLByFrom);
                    }
                    _sqlSearchInProgress = false;
                }

            }
            else if (e.TabPage == stackTraceTab)
            {
                processSearchBoxDisplay(stackTraceListView, stackTraceFindBox, new KeyEventArgs(Keys.Escape));
                if (_traceSearchInProgress)
                {
                    UIBuilder.BuildStackTraceList(stackTraceListView, traceData.StackTraces);
                    _traceSearchInProgress = false;
                }

            }

        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new SettingsForm().ShowDialog(this);
            UpdateUI();
        }

        private void handleStatisticDoubleClick(object sender, MouseEventArgs args)
        {
            ListView view = (ListView)sender;
            if (view.SelectedItems.Count > 0 && view.SelectedItems[0].Tag != null)
            {
                var tag = view.SelectedItems[0].Tag;

                if (tag.GetType().Equals(typeof(SQLStatement)))
                {
                    GoToSQLStatementInExecPath((SQLStatement)tag);
                }
            }
        }

        private void MainForm_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (processor != null)
            {
                if (e.KeyChar == '\u001b')
                {
                    processor.CancelAsync();
                    e.Handled = true;
                }
            }
        }

        private void executionSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (IsRunningMono || traceData == null)
            {
                return;
            }
            if ((e.Control && e.KeyCode == Keys.F) || (e.KeyCode == Keys.Enter && _findBoxVisible) || (e.KeyCode == Keys.Escape && _findBoxVisible))
            {
                processSearchBoxDisplay(executionTree, execFindBox, e);

                if (_findBoxVisible)
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        // time to do the search.
                        SearchExecutionCalls(executionTree, execFindBox.Text);
                        _execSearchInProgress = true;
                    }
                }
                else
                {
                    RestoreExecutionNodes();
                    _execSearchInProgress = false;
                }
            }

        }

        private void processSearchBoxDisplay(Control parentControl, TextBox searchBox, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                if (_findBoxVisible)
                {
                    searchBox.Visible = false;
                    if (parentControl.Dock == DockStyle.None)
                    {
                        parentControl.Height += searchBox.Height - 2;
                    }
                    parentControl.Dock = DockStyle.Fill;
                    parentControl.Focus();
                }
                else
                {
                    if (parentControl.Dock == DockStyle.Fill)
                    {
                        parentControl.Height -= searchBox.Height - 2;
                    }
                    parentControl.Dock = DockStyle.None;
                    searchBox.SelectAll();
                    searchBox.Visible = true;
                    searchBox.Focus();
                    searchBox.SelectAll();
                }
                _findBoxVisible = !_findBoxVisible;
            }

            if (e.KeyCode == Keys.Escape)
            {
                searchBox.Visible = false;
                parentControl.Height += searchBox.Height - 2;
                parentControl.Dock = DockStyle.Fill;
                parentControl.Focus();
                _findBoxVisible = false;
            }

        }

        private void SearchExecutionCalls(TreeView tree, string searchText)
        {
            /* ToDo: Alternate search mechanism for mono */
            if (Type.GetType("Mono.Runtime") != null)
            {
                MessageBox.Show("Due to poor performance of TreeView in Mono, search is currently disabled for Execution Path.");
                return;
            }
            tree.BeginUpdate();
            tree.Nodes.Clear();
            UIBuilder.BuildExecutionTree(traceData, executionTree, SQLMapToTree, ExecCallToTree);
            if (searchText.Trim().Length == 0)
            {
                tree.EndUpdate();
                return;
            }
            var searchTextLower = searchText.ToLower();

            var matches = traceData.AllExecutionCalls.Where(c => (c.StartLine + c.Function).ToLower().Contains(searchTextLower)).ToList();

            foreach (ExecutionCall m in matches)
            {
                BuildOutTreeForCall(m);
            }

            FilterExecutionNodes(tree, searchTextLower);

            tree.ExpandAll();
            tree.EndUpdate();
        }

        private void FilterExecutionNodes(TreeView tree, string searchText)
        {
            Stack<TreeNode> toProcess = new Stack<TreeNode>();
            string searchTextLower = searchText.ToLower();
            foreach (TreeNode t in tree.Nodes)
            {
                toProcess.Push(t);
            }

            while (toProcess.Count > 0)
            {
                TreeNode current = toProcess.Pop();
                if (current.Text.ToLower().Contains(searchTextLower))
                {
                    current.BackColor = Color.LightBlue;

                    foreach (TreeNode t in current.Nodes)
                    {
                        toProcess.Push(t);
                    }
                    continue;
                }
                if (current.Nodes.Count == 0)
                {
                    /* this is a leaf node, if no match, remove */
                    if (current.Text.ToLower().Contains(searchTextLower) == false)
                    {
                        current.Parent.Nodes.Remove(current);
                    }
                }
                else
                {
                    if (NodeHasChildThatMatches(current, searchTextLower) == false)
                    {
                        /* has children but no matches */
                        if (current.Parent == null)
                        {
                            tree.Nodes.Remove(current);
                        }
                        else
                        {
                            current.Parent.Nodes.Remove(current);
                        }
                    }
                    else
                    {
                        foreach (TreeNode t in current.Nodes)
                        {
                            toProcess.Push(t);
                        }
                    }
                }
            }
        }

        private bool NodeHasChildThatMatches(TreeNode node, string searchText)
        {
            Stack<TreeNode> toProcess = new Stack<TreeNode>();
            toProcess.Push(node);
            bool _found = false;
            while (toProcess.Count > 0)
            {
                TreeNode current = toProcess.Pop();
                foreach (TreeNode t in current.Nodes)
                {
                    if (t.Text.ToLower().Contains(searchText))
                    {
                        _found = true;
                    }
                    else
                    {
                        toProcess.Push(t);
                    }
                }
            }
            return _found;
        }


        private void RestoreExecutionNodes()
        {
            UIBuilder.BuildExecutionTree(traceData, executionTree, SQLMapToTree, ExecCallToTree);
        }

        private void sqlFindBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (traceData == null)
            {
                return;
            }
            if ((e.Control && e.KeyCode == Keys.F) || (e.KeyCode == Keys.Enter && _findBoxVisible) || (e.KeyCode == Keys.Escape && _findBoxVisible))
            {
                processSearchBoxDisplay(sqlListView, sqlFindBox, e);

                if (_findBoxVisible)
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        // time to do the search.
                        if (currentSQLDisplay == SQLDisplayType.ALL)
                        {
                            UIBuilder.BuildAllSQLList(sqlListView, traceData.SQLStatements.Where(p => (p.LineNumber + p.Statement).ToLower().Contains(sqlFindBox.Text.ToLower())).ToList());
                        }
                        else if (currentSQLDisplay == SQLDisplayType.WHERE)
                        {
                            UIBuilder.BuildWhereSQLList(sqlListView, traceData.SQLByWhere.Where(p => p.WhereClause.ToLower().Contains(sqlFindBox.Text.ToLower())).ToList());
                        }
                        else if (currentSQLDisplay == SQLDisplayType.FROM)
                        {
                            UIBuilder.BuildFromSQLList(sqlListView, traceData.SQLByFrom.Where(p => p.FromClause.ToLower().Contains(sqlFindBox.Text.ToLower())).ToList());
                        }
                        _sqlSearchInProgress = true;
                    }
                }
                else
                {
                    if (currentSQLDisplay == SQLDisplayType.ALL)
                    {
                        UIBuilder.BuildAllSQLList(sqlListView, traceData.SQLStatements);
                    }
                    else if (currentSQLDisplay == SQLDisplayType.WHERE)
                    {
                        UIBuilder.BuildWhereSQLList(sqlListView, traceData.SQLByWhere);
                    }
                    else if (currentSQLDisplay == SQLDisplayType.FROM)
                    {
                        UIBuilder.BuildFromSQLList(sqlListView, traceData.SQLByFrom);
                    }
                    _sqlSearchInProgress = false;
                }
            }
        }

        private void stackTraceListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (traceData == null)
            {
                return;
            }
            if ((e.Control && e.KeyCode == Keys.F) || (e.KeyCode == Keys.Enter && _findBoxVisible) || (e.KeyCode == Keys.Escape && _findBoxVisible))
            {
                processSearchBoxDisplay(stackTraceListView, stackTraceFindBox, e);

                if (_findBoxVisible)
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        // time to do the search.
                        UIBuilder.BuildStackTraceList(stackTraceListView, traceData.StackTraces.Where(s => (s.Message + s.Offender + s.LineNumber).ToLower().Contains(stackTraceFindBox.Text.ToLower())).ToList());
                        _traceSearchInProgress = true;
                    }
                }
                else
                {
                    UIBuilder.BuildStackTraceList(stackTraceListView, traceData.StackTraces);
                    _traceSearchInProgress = false;
                }
            }
        }

        private void mainTabStrip_KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Control && e.KeyCode == Keys.F) || (e.KeyCode == Keys.Enter && _findBoxVisible) || (e.KeyCode == Keys.Escape && _findBoxVisible))
            {
                var page = mainTabStrip.SelectedTab;

                if (page == executionPathTab)
                {
                    executionSearchKeyDown(executionTree, e);
                }
                else if (page == sqlStatementsTab)
                {
                    sqlFindBox_KeyDown(sqlListView, e);
                }
                else if (page == stackTraceTab)
                {
                    stackTraceListView_KeyDown(stackTraceListView, e);
                }
                e.Handled = true;
            }
        }

        private void sqlItemContextStrip_Opening(object sender, CancelEventArgs e)
        {
            if (sqlListView.SelectedItems.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            var item = sqlListView.SelectedItems[0];

            if (item.Tag.GetType().Equals(typeof(SQLStatement)) == false)
            {
                e.Cancel = true;
                return;
            }

            var sqlStatment = item.Tag as SQLStatement;

            if (sqlStatment.BindValues.Count == 0)
            {
                copyBindsToolStripMenuItem.Enabled = false;
                copyResolvedStatementToolStripMenuItem.Enabled = false;
            }
            else
            {
                copyBindsToolStripMenuItem.Enabled = true;
                copyResolvedStatementToolStripMenuItem.Enabled = true;
            }


        }

        private void copyStatementToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var sqlStatement = sqlListView.SelectedItems[0].Tag as SQLStatement;

            if (IsRunningOSX)
            {
                OSXClipboard.CopyToClipboard(sqlStatement.Statement);
            } else
            {
                Clipboard.SetText(sqlStatement.Statement);
            }

            MessageBox.Show("SQL Statement copied to clipboard.");
        }

        private void copyBindsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var sqlStatement = sqlListView.SelectedItems[0].Tag as SQLStatement;

            StringBuilder sb = new StringBuilder();

            foreach (var b in sqlStatement.BindValues)
            {
                sb.AppendLine(String.Format("Bind {0} = {1}", b.Index, b.Value));
            }

            if (IsRunningOSX)
            {
                OSXClipboard.CopyToClipboard(sb.ToString());
            } else
            {
                Clipboard.SetText(sb.ToString());
            }

            MessageBox.Show("Bind values copied to clipboard.");
        }

        private void copyResolvedStatementToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var sqlStatement = sqlListView.SelectedItems[0].Tag as SQLStatement;

            string workingStatement = sqlStatement.Statement;

            foreach (var b in sqlStatement.BindValues.OrderBy(p => p.Index).Reverse())
            {
                var valueString = "";
                if (b.Type == 19)
                {
                    valueString = b.Value;
                }
                else
                {
                    valueString = "'" + b.Value + "'";
                }
                workingStatement = workingStatement.Replace(":" + b.Index, valueString);
            }

            if (IsRunningOSX)
            {
                OSXClipboard.CopyToClipboard(workingStatement);
            }
            else
            {
                Clipboard.SetText(workingStatement);
            }

            MessageBox.Show("Resolved statement copied to clipboard.");
        }

        private void executionContextStrip_Opening(object sender, CancelEventArgs e)
        {
            if (executionTree.SelectedNode == null)
            {
                e.Cancel = true;
                return;
            }
            var item = executionTree.SelectedNode;

            /*if (item.Tag.GetType().Equals(typeof(ExecutionCall)) == false)
            {
                e.Cancel = true;
                return;
            }*/
        }

        private void copyStackTraceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            bool _isSQL = false;
            var item = executionTree.SelectedNode.Tag as ExecutionCall;
            if (item == null)
            {
                _isSQL = true;
                item = (executionTree.SelectedNode.Tag as SQLStatement).ParentCall;
            }
            if (item != null)
            {
                var stackTrace = item.GetStackTrace();

                StringBuilder sb = new StringBuilder();

                for (var x = 0; x < stackTrace.Count; x++)
                {
                    sb.Append(new string(' ', 4 * x));
                    sb.AppendLine(String.Format("{0}", stackTrace[x].Item2));
                }

                if (_isSQL)
                {
                    /* write out the SQL */
                    var sqlItem = executionTree.SelectedNode.Tag as SQLStatement;
                    sb.Append(new string(' ', 4 * stackTrace.Count));
                    sb.AppendLine(String.Format("{0}", sqlItem.Statement));
                }
                if (IsRunningOSX)
                {
                    OSXClipboard.CopyToClipboard(sb.ToString());
                } else
                {
                    Clipboard.SetText(sb.ToString());
                }
                
                MessageBox.Show("Stack trace copied successfully.");
            }




        }

        private void copyStackTraceToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            var sqlStatement = sqlListView.SelectedItems[0].Tag as SQLStatement;



            var item = sqlStatement.ParentCall;
            if (item != null)
            {
                var stackTrace = item.GetStackTrace();

                StringBuilder sb = new StringBuilder();

                for (var x = 0; x < stackTrace.Count; x++)
                {
                    sb.Append(new string(' ', 4 * x));
                    sb.AppendLine(String.Format("{0}", stackTrace[x].Item2));
                }

                /* write out the SQL */
                sb.Append(new string(' ', 4 * stackTrace.Count));
                sb.AppendLine(String.Format("{0}", sqlStatement.Statement));

                if (IsRunningOSX)
                {
                    OSXClipboard.CopyToClipboard(sb.ToString());
                } else
                {
                    Clipboard.SetText(sb.ToString());
                }

                MessageBox.Show("Stack trace copied successfully.");
            }
        }

        private void SQLView_Clicked(object sender, EventArgs e)
        {
            if (traceData == null)
            {
                return;
            }
            var menu = sender as ToolStripMenuItem;
            var currentStatus = menu.Checked;

            /* reset all menu's */
            allToolStripMenuItem.Checked = false;
            byWhereClauseToolStripMenuItem.Checked = false;
            byFromClauseToolStripMenuItem.Checked = false;
            errorsToolStripMenuItem.Checked = false;

            menu.Checked = currentStatus;

            /* Remove the ListItemViewComparer if one is present */
            sqlListView.ListViewItemSorter = null;

            if (sender == allToolStripMenuItem)
            {
                mainTabStrip.SelectedTab = sqlStatementsTab;

                UIBuilder.BuildAllSQLList(sqlListView, traceData.SQLStatements);
                sortAscending = true;
                sqlListView.ListViewItemSorter = new ListViewItemComparer(0, sortAscending);
                previousSortColumn = 0;

                currentSQLDisplay = SQLDisplayType.ALL;
            }
            else if (sender == byWhereClauseToolStripMenuItem)
            {
                mainTabStrip.SelectedTab = sqlStatementsTab;
                UIBuilder.BuildWhereSQLList(sqlListView, traceData.SQLByWhere);

                sortAscending = true;
                sqlListView.ListViewItemSorter = new ListViewItemComparer(2, sortAscending);
                previousSortColumn = 0;
                currentSQLDisplay = SQLDisplayType.WHERE;
            }
            else if (sender == byFromClauseToolStripMenuItem)
            {
                mainTabStrip.SelectedTab = sqlStatementsTab;
                UIBuilder.BuildFromSQLList(sqlListView, traceData.SQLByFrom);

                sortAscending = true;
                sqlListView.ListViewItemSorter = new ListViewItemComparer(2, sortAscending);
                previousSortColumn = 0;
                currentSQLDisplay = SQLDisplayType.WHERE;

            }
            else if (sender == errorsToolStripMenuItem)
            {
                mainTabStrip.SelectedTab = sqlStatementsTab;
                UIBuilder.BuildAllSQLList(sqlListView, traceData.SQLStatements.Where<SQLStatement>(s => s.IsError == true).ToList());
                previousSortColumn = 0;
                currentSQLDisplay = SQLDisplayType.ALL;
            }
            else if (sender == tablesToolStripMenuItem)
            {
                mainTabStrip.SelectedTab = sqlStatementsTab;
                UIBuilder.BuildSQLTableList(sqlListView, traceData.SQLStatements);
                previousSortColumn = 0;
                currentSQLDisplay = SQLDisplayType.ALL;
            }
        }

        private void openInNewWindowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var newWindow = new MainForm(true);

            newWindow.Show();

            newWindow.Top = this.Top + 75;
            newWindow.Left = this.Left + 75;
        }

        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (traceData == null)
            {
                return;
            }
            saveFileDialog1.FileName = "";
            var result = saveFileDialog1.ShowDialog();
            if (result == DialogResult.OK)
            {
                var filename = saveFileDialog1.FileName;

                new TraceSerializer().SerializeTraceData(traceData, new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None));
                MessageBox.Show("Data saved successfully!");
            }
        }

        private void executionTree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var call = e.Node.Tag as ExecutionCall;

            if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Text.Equals("Loading..."))
            {
                // build out nodes for call
                BuildExpandingNode(e.Node, call);
            }
        }

        private void BuildExpandingNode(TreeNode node, ExecutionCall call)
        {
            node.Nodes.Clear();
            foreach (var child in call.Children)
            {
                UIBuilder.BuildExecutionTree(node, child, SQLMapToTree, ExecCallToTree, true);
            }
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            if (progressBar.GetCurrentParent() != null)
            {
                progressBar.Width = progressBar.GetCurrentParent().Width - 120;
            }
        }

        private void compareTracesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new CompareDialog(traceData).ShowDialog(this);
        }
    }

    enum SQLDisplayType
    {
        ALL, WHERE, FROM
    }

    class ListViewItemComparer : IComparer
    {
        private int col;
        private bool ascending;

        public ListViewItemComparer()
        {
            col = 0;
            ascending = true;
        }
        public ListViewItemComparer(int column, bool ascending)
        {
            col = column;
            this.ascending = ascending;
        }
        public int Compare(object x, object y)
        {
            var text1 = ((ListViewItem)x).SubItems[col].Text;
            var text2 = ((ListViewItem)y).SubItems[col].Text;

            double number1 = 0;
            double number2 = 0;

            if (double.TryParse(text1, out number1) && double.TryParse(text2, out number2))
            {
                if (ascending)
                {
                    return number1.CompareTo(number2);
                }
                else
                {
                    return number2.CompareTo(number1);
                }
            }

            if (ascending)
            {
                return String.Compare(text1, text2);
            }
            else
            {
                return String.Compare(text2, text1);
            }
        }
    }
}
