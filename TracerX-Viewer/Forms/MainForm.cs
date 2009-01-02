using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Threading;
using System.IO;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using TracerX.Properties;
using TracerX.Forms; 

// See http://blogs.msdn.com/cumgranosalis/archive/2006/03/06/VirtualListViewUsage.aspx
// for a good article on using ListView in virtual mode.

// SvnBridge: https://tfs05.codeplex.com, id_cp

namespace TracerX.Viewer {
    // This is the main form for the TracerX log viewer.
    [System.Diagnostics.DebuggerDisplay("MainForm")] // Helps prevent debugger from freezing in the worker thread.
    internal partial class MainForm : Form {
        #region Ctor/init
        // Constructor.  args[0] may contain the log file path to load.
        public MainForm(string[] args) {
            InitializeComponent();

            crumbPanel1.Clear();

            _originalTitle = this.Text;
            this.Icon = Properties.Resources.scroll_view;
            Thread.CurrentThread.Name = "Main";
            TheMainForm = this;

            EventHandler filterChange = new EventHandler(FilterAddedOrRemoved);
            ThreadName.AllVisibleChanged += filterChange;
            ThreadObject.AllVisibleChanged += filterChange;
            LoggerObject.AllVisibleChanged += filterChange;
            FilterDialog.TextFilterOnOff += filterChange;

            _refreshTimer.Tick += new EventHandler(_refreshTimer_Tick);

            InitColumns();

            if (Settings.Default.IndentChar == '\0') Settings.Default.IndentChar = ' ';

            // Setting the FileState affects many menu items and buttons.
            _FileState = FileState.NoFile;

            // The check state of relativeTimeButton is driven by Settings.Default.RelativeTime;
            relativeTimeButton.Checked = Settings.Default.RelativeTime;
            absoluteTimeButton.Checked = !Settings.Default.RelativeTime;

            enableColorsBtn.Checked = Settings.Default.ColoringEnabled;
            disableColorsBtn.Checked = !Settings.Default.ColoringEnabled;
            enableColoringMenu.Visible = !Settings.Default.ColoringEnabled;
            disableColoringMenu.Visible = Settings.Default.ColoringEnabled;

            Settings.Default.PropertyChanged += Settings_PropertyChanged;

            if (Settings.Default.ColoringRules != null) {
                foreach (ColoringRule rule in Settings.Default.ColoringRules) rule.MakeReady();
            }

            if (args.Length > 0) {
                StartReading(args[0]);
            }

            VersionChecker.CheckForNewVersion();
        }

        // Perform column-related initialization.
        private void InitColumns() {
            // Keep a copy of the original list of columns since the only way to
            // hide a column in a ListView is to remove it.
            OriginalColumns = new ColumnHeader[TheListView.Columns.Count];
            for (int i = 0; i < TheListView.Columns.Count; ++i) {
                OriginalColumns[i] = TheListView.Columns[i];
            }

            // Apply the persisted column settings.
            if (Settings.Default.ColIndices != null &&
                Settings.Default.ColSelections != null &&
                Settings.Default.ColWidths != null && 
                Settings.Default.ColWidths.Length == OriginalColumns.Length) 
            {
                TheListView.Columns.Clear();
                try {
                    for (int i = 0; i < OriginalColumns.Length; ++i) {
                        if (Settings.Default.ColSelections[i]) {
                            TheListView.Columns.Add(OriginalColumns[i]);
                        }
                        OriginalColumns[i].Width = Settings.Default.ColWidths[i];
                    }

                    // We can't set the display index until after the column headers
                    // have been added.
                    for (int i = 0; i < OriginalColumns.Length; ++i) {
                        OriginalColumns[i].DisplayIndex = Settings.Default.ColIndices[i];
                    }
                } catch {
                    // If something goes wrong, just show all columns.
                    TheListView.Columns.Clear();
                    TheListView.Columns.AddRange(OriginalColumns);
                }
            }
        }
        #endregion

        #region Public
        // This gives other classes access to the MainForm instance.
        public static MainForm TheMainForm;

        // The original columns in the ListView before any are hidden.
        public ColumnHeader[] OriginalColumns;

        // These are the trace levels that exist in the current file.
        public TraceLevel ValidTraceLevels {
            get { return _reader.LevelsFound; }
        }

        // The timestamp from the row selected to be the "zero time" row.
        public static DateTime ZeroTime = DateTime.MinValue;

        public Row[] Rows { get { return _rows; } }

        // Track which trace levels are visible (not filtered out).
        public TraceLevel VisibleTraceLevels {
            get { return _visibleTraceLevels; }
            set {
                if (_visibleTraceLevels != value) {
                    // If changing to or from all levels being visible (_reader.LevelsFound),
                    // call FilterAddedOrRemoved.
                    TraceLevel oldVal = _visibleTraceLevels;
                    _visibleTraceLevels = value;
                    if (_visibleTraceLevels == _reader.LevelsFound || oldVal == _reader.LevelsFound) {
                        FilterAddedOrRemoved(null, null);
                    }
                }
            }
        }

        // The Row corresponding to the the ListViewItem that has focus, or the first
        // row if no item has the focus.  Used as the start of a Find.
        public Row FocusedRow {
            get {
                if (TheListView.FocusedItem == null) {
                    return _rows[0];
                } else {
                    return _rows[TheListView.FocusedItem.Index];
                }
            }
        }

        // The Row corresponding to the the ListViewItem that has focus.
        // Null if no items are selected or no item has focus.
        public Row CurrentRow {
            get {
                if (TheListView.FocusedItem == null || TheListView.SelectedIndices.Count == 0) {
                    return null;
                } else {
                    return _rows[TheListView.FocusedItem.Index];
                }
            }
        }

        public int NumRows {
            get { return TheListView.VirtualListSize; }
            
            set {
                try {
                    // This can throw an exception when the user selects a row near the
                    // end of the file and hides a thread with lots of records.  It seems
                    // to be a bug in ListView.  The app seems to function OK as long as we catch
                    // and ignore the exception.
                    TheListView.VirtualListSize = value;
                } catch (Exception ex) {
                    Debug.Print("Exception setting ListView.VirtualListSize: " + ex.ToString());
                }

                // Disable Find and FindNext/F3 if no text is visible.
                UpdateFindCommands();

                Debug.Print("NumRows now " + NumRows);
            }
        }

        public Row SelectSingleRow(int rowIndex) {
            SelectSingleItem(_rows[rowIndex]);
            return _rows[rowIndex];
        }

        public void ClearItemCache() {
            Debug.Print("Clearing _itemCache.");
            _itemCache = null; // Cause cache to be rebuilt.
        }

        public void GetVirtualItem(Object sender, RetrieveVirtualItemEventArgs e) {
            bool newItem;
            ViewItem item = GetListItem(e.ItemIndex, out newItem);
            e.Item = item;
            
            // When the main form is not active, the blue highlighting for selected items disappears.
            // To prevent that, we explicitly set the item's colors.  The selected items (and 
            // the scroll position) can change
            // while the form is not active (e.g. when the user clicks Find Next in the find dialog),
            // so it is not sufficient to just set the colors in the Activated and Deactivated events.
            // New items are created with the correct colors, so we only call SetItemColors for
            // existing items.
            if (!newItem && this != Form.ActiveForm) item.SetItemColors(false);
        }

        public void InvalidateTheListView() {
            ClearItemCache();
            TheListView.Invalidate();
        }

        // Called by the FindDialog and when F3 is pressed.
        // If the specified text is found (not bookmarked), selects the row/item
        // and returns the ListViewItem.
        public Row DoSearch(StringMatcher matcher, bool searchUp, bool bookmark) {
            Cursor restoreCursor = this.Cursor;
            Row startRow = FocusedRow;
            Row curRow = startRow;
            bool found = false;

            // Remember inputs in case user hits F3 or shift+F3.
            _textMatcher = matcher; 

            UpdateFindCommands();

            try {
                this.Cursor = Cursors.WaitCursor;

                do {
                    curRow = NextRow(curRow, searchUp);

                    if (matcher.Matches(curRow.ToString())) {
                        found = true;
                        if (bookmark) {
                            curRow.IsBookmarked = true;
                        } else {
                            SelectSingleItem(curRow);
                            return curRow;
                        }
                    }
                } while (curRow != startRow);
            } finally {
                this.Cursor = restoreCursor;
            }

            if (!found) {
                MessageBox.Show("Did not find: " + matcher.Needle);
            } else if (bookmark) {
                bookmarkNextCmd.Enabled = bookmarkPrevCmd.Enabled = bookmarkClearCmd.Enabled = true;
                InvalidateTheListView();
            }

            return null;
        }

        // Find the index of the last visible item displayed by TheListView.
        public int FindLastVisibleItem() {
            int ndx = TheListView.TopItem.Index;
            do {
                ++ndx;
            } while (ndx < NumRows && TheListView.ClientRectangle.IntersectsWith(TheListView.Items[ndx].Bounds));

            //Debug.Print("Last visible index = " + (ndx - 1));
            return ndx - 1;
        }

        // Called by the FilterDialog when the user clicks Apply or OK.
        public void RebuildAllRows() {
            RebuildRows(0, _records[0]);
        }
        #endregion

        private enum FileState { NoFile, Loading, Loaded };
        private FileState _fileState = FileState.NoFile;

        // The current log file.
        private FileInfo _fileInfo;

        // Helper that reads the log file.
        private Reader _reader;

        // List of records read from the log file.
        private List<Record> _records;

        // Array of rows being displayed by the ListView. 
        // This gets regenerated when the filter changes and
        // when rows are expanded or collapsed.
        private Row[] _rows;

        // Timer that drives the auto-refresh feature.
        private System.Windows.Forms.Timer _refreshTimer = new System.Windows.Forms.Timer();

        // The row number (scroll position) to restore after refreshing the file.
        private int _rowNumToRestore;

        // The original title to which the file name is appended to be
        // displayed in the title bar.
        private string _originalTitle;

        // The trace levels selected to be visible (not filtered out).
        private TraceLevel _visibleTraceLevels;

        // Text search settings for F3 ("find next").
        StringMatcher _textMatcher;

        // Cache of ListViewItems used to improve the performance of the ListView control
        // in virtual mode (it tends to request the same item many times).
        private ViewItem[] _itemCache;

        // Index of first item in _itemCache.
        private int _firstItemIndex; 

        // The area occupied by the ListView header.  Used to determine which column header is
        // right-clicked so the appropriate context menu can be displayed.
        private Rectangle _headerRect;

        private FileState _FileState {
            get { return _fileState; }
            set { 
                _fileState = value;
                startAutoRefresh.Enabled = (_fileState == FileState.Loaded && !stopAutoRefresh.Enabled);
                propertiesCmd.Enabled = (_fileState == FileState.Loaded);
                filterDlgCmd.Enabled = (_fileState == FileState.Loaded);
                closeToolStripMenuItem.Enabled = (_fileState == FileState.Loaded);
                refreshCmd.Enabled = (_fileState == FileState.Loaded);
                expandAllButton.Enabled = (_FileState == FileState.Loaded);
                exportToCSVToolStripMenuItem.Enabled = (_FileState == FileState.Loaded);

                buttonStop.Visible = (_fileState == FileState.Loading);

                openFileCmd.Enabled = (_fileState != FileState.Loading);

                if (_fileState != FileState.Loaded) {
                    NumRows = 0;
                    _rows = null;
                    _records = null;
                    toolStripProgressBar1.Value = 0;

                    crumbPanel1.Clear();

                    // Some commands are disabled when filestate != Loaded, but not necessarily
                    // enabled when filestate == Loaded.
                    filterClearCmd.Enabled = false;
                    bookmarkToggleCmd.Enabled = false;
                    bookmarkNextCmd.Enabled = false;
                    bookmarkPrevCmd.Enabled = false;
                    bookmarkClearCmd.Enabled = false;
                }

                UpdateFindCommands();
            } 
        }

        // True if the file has changed since the last call.
        private bool _FileChanged {
            get {
                DateTime oldtime = _fileInfo.LastWriteTime;
                _fileInfo.Refresh();
                bool ret = (oldtime != _fileInfo.LastWriteTime);
                Debug.Print("FileChanged = " + ret);
                return ret;
            }
        }

        // This returns an array of the ColumnHeaders in the order they are
        // displayed by the ListView.  Used to determine which column header
        // was right-clicked.
        public ColumnHeader[] OrderedHeaders {
            get {
                ColumnHeader[] arr = new ColumnHeader[TheListView.Columns.Count];

                foreach (ColumnHeader col in TheListView.Columns) {
                    arr[col.DisplayIndex] = col;
                }

                return arr;
            }
        }

        private Record _FocusedRec {
            get {
                Row focusedRow = FocusedRow;
                if (focusedRow == null) {
                    return null;
                } else {
                    return focusedRow.Rec;
                }
            }
        }
        
        // This is supposed to make the listview highlight the selected
        // item even when it doesn't have the focus, but it doesn't work.
        //private void SetListViewStyle() {
        //    const int GWL_STYLE = (-16);
        //    const long long_LVS_SHOWSELALWAYS = 8;
        //    const int int_LVS_SHOWSELALWAYS = 8;

        //    if (IntPtr.Size == 8) {
        //        long style = GetWindowLongPtr(TheListView.Handle, GWL_STYLE);
        //        long success = SetWindowLongPtr(TheListView.Handle, GWL_STYLE, (style | long_LVS_SHOWSELALWAYS));
        //        style = GetWindowLongPtr(TheListView.Handle, GWL_STYLE);
        //    } else {
        //        int style = GetWindowLong(TheListView.Handle, GWL_STYLE);
        //        int sucess = SetWindowLong(TheListView.Handle, GWL_STYLE, style | int_LVS_SHOWSELALWAYS);
        //        style = GetWindowLong(TheListView.Handle, GWL_STYLE);
        //    }
        //}

        //[DllImport("user32.dll", SetLastError = true)]
        //static extern int GetWindowLong(IntPtr hWnd, int nIndex); 

        //[DllImport("user32.dll", SetLastError = true)]
        //private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

        //[DllImport("user32.dll", SetLastError = true)]
        //private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        //[DllImport("user32.dll", SetLastError = true)]
        //private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

        //// Returns true if the specified text matches the current search/find settings.
        //private bool IsMatch(string curText, string needle, StringComparison compareType, Regex regex) {
        //    if (regex == null) {
        //        return (curText.IndexOf(needle, compareType) != -1);
        //    } else {
        //        return regex.IsMatch(curText);
        //    }
        //}

        // Increment or decrement _curRow depending on searchUp and handle wrapping.
        private Row NextRow(Row curRow, bool searchUp) {
            int ndx = curRow.Index;
            if (searchUp) {
                --ndx;
                if (ndx < 0) ndx = NumRows - 1;
            } else {
                ndx = (ndx + 1) % NumRows;
            }

            return _rows[ndx];
        }

        private void SelectSingleItem(Row row) {
            TheListView.SelectedIndices.Clear();
            TheListView.EnsureVisible(row.Index);
            ListViewItem item = TheListView.Items[row.Index];
            item.Focused = true;
            item.Selected = true;
            //if (this != Form.ActiveForm) 
            //    SetItemColors(item, true);
        }

        #region File loading
        // This gets the row number to restore after refreshing the file.
        private int GetRowNumToRestore() {
            int ret = 0;
            if (TheListView.FocusedItem != null && TheListView.FocusedItem.Index == NumRows - 1) {
                ret = -1; // Special value meaning the very end.
            } else if (TheListView.TopItem != null) {
                ret = TheListView.TopItem.Index;
            }

            return ret;
        }

        // Open the specified log file and, if successfull, 
        // start the background thread that reads it.
        // A null filename means to refresh the current file.
        private void StartReading(string filename) {
            Reader prospectiveReader = new Reader();
            bool refreshing;

            if (filename == null) {
                filename = _fileInfo.FullName;
                refreshing = true;
            } else {
                refreshing = false;
            }
                
            _rowNumToRestore = GetRowNumToRestore();

            // If we can't open the new file, the old file stays loaded.
            if (prospectiveReader.OpenLogFile(filename)) {
                _refreshTimer.Stop();
                _FileState = FileState.Loading;

                if (refreshing && Settings.Default.KeepFilter) {
                    prospectiveReader.ReuseFilters();

                    // Temporarily set _visibleTraceLevels to those that should be visible
                    // after reading the file.  Basically, we want to see the ones that are
                    // currently visible plus any new ones we find after reading the file.
                    // The ones found in the current file (ValidTraceLevels) that are currently
                    // hidden should stay hidden.
                    _visibleTraceLevels |= ~ValidTraceLevels;
                } else {
                    // Show all trace levels.
                    _visibleTraceLevels |= ~_visibleTraceLevels;
                    FilterDialog.TextFilterDisable();
                }

                _reader = prospectiveReader; // Must come after references to ValidTraceLevels.
                ResetFilters();
                _fileInfo = new FileInfo(filename);
                DateTime dummy = _fileInfo.LastWriteTime; // Makes FileChanged work correctly.

                filterClearCmd.Enabled = false;
                filenameLabel.Text = filename;

                backgroundWorker1.RunWorkerAsync();
            }
        }

        // This method runs in a worker thread.
        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e) {
            if (Thread.CurrentThread.Name == null) Thread.CurrentThread.Name = "Background Worker";

            int percent = 0;
            int rowCount = 0;
            Record lastNonCircularRecord = null;
            long totalBytes = _reader.FileReader.BaseStream.Length;

            _records = new List<Record>((int)(totalBytes / 200)); // Guess at how many records

            Record record = _reader.ReadRecord();
            while (record != null) {
                rowCount += record.Lines.Length;
                record.Index = _records.Count;
                _records.Add(record);
                if (!_reader.InCircularPart) lastNonCircularRecord = record;

                percent = (int)((_reader.BytesRead * 100) / totalBytes);
                if (percent != toolStripProgressBar1.Value) {
                    backgroundWorker1.ReportProgress(percent);
                }

                record = _reader.ReadRecord();

                // This Sleep call is critical.  Without it, the main thread doesn't seem to
                // get any cpu time and the worker thread seems to go much slower.
                Thread.Sleep(0);

                if (backgroundWorker1.CancellationPending) {
                    Debug.Print("Background worker was cancelled.");
                    e.Cancel = true;
                    break;
                }
            }

            // If the log has both a circular part and a non-circular part, there may
            // be missing exit/entry records due to wrapping.
            if (_reader.InCircularPart && lastNonCircularRecord != null) {
                rowCount += InsertMissingRecords(lastNonCircularRecord);
            }

            Debug.Print("Closing log file.");
            _reader.CloseLogFile();

            // Initially, there is a 1:1 relationship between each row and each record.  This will change
            // if the user collapses method calls (causing some records to be omitted from view) or
            // expands rows with embedded newlines.
            // Allocate enough rows to handle the case of all messages with embedded newlines being expanded.
            _rows = new Row[rowCount];
        }

        // This inserts an exit record for every entry record in the non-circular part of the log
        // whose corresponding exit record was lost due to wrapping. It also inserts an entry
        // record for every exit record in the circular part of the log whose corresponding entry
        // record was lost due to wrapping.
        private int InsertMissingRecords(Record lastNonCircularRecord) {
            Record firstCircularRecord = _records[lastNonCircularRecord.Index + 1];
            List<Record> generatedExitRecords = _reader.GetMissingExitRecords();
            List<Record> generatedEntryRecords = _reader.GetMissingEntryRecords();

            // Set certain fields of the generated records based on the last non-circular record.
            // The exit records are always inserted before the entry records, just after the last
            // non-circular record, so the MsgNum is contiguous from there.
            uint msgNum = lastNonCircularRecord.MsgNum;
            foreach (Record missingRec in generatedExitRecords) {
                missingRec.MsgNum = ++msgNum;
                missingRec.Time = lastNonCircularRecord.Time;
            }

            // Now patch up the generated entry records so they appear to come just before the
            // first true circular record.  Any gap in the resulting MsgNum values will appear
            // between the exit records and the entry records.
            msgNum = firstCircularRecord.MsgNum - (uint)generatedEntryRecords.Count;
            foreach (Record missingRec in generatedEntryRecords) {
                missingRec.MsgNum = msgNum++;
                missingRec.Time = firstCircularRecord.Time;
            }

            // Concatenate the generated exit and entry records and 
            // insert them into the actual records.
            generatedExitRecords.AddRange(generatedEntryRecords);
            generatedEntryRecords = null;

            if (generatedExitRecords.Count > 0) {
                _records.InsertRange(lastNonCircularRecord.Index + 1, generatedExitRecords);

                // The Index of each inserted and subsequent record must be adjusted due to the insertion.
                for (int i = lastNonCircularRecord.Index + 1; i < _records.Count; ++i) {
                    _records[i].Index = i;
                }
            }

            return generatedExitRecords.Count;
        }

        private void backgroundWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e) {
            if (e.Error == null && !e.Cancelled) {
                toolStripProgressBar1.Value = 100;
                pctLabel.Text = "100%";
            }

            _FileState = FileState.Loaded;

            AddFileToRecentlyViewed(_fileInfo.FullName);
            this.Text = _fileInfo.Name + " - " + _originalTitle;

            _visibleTraceLevels &= ValidTraceLevels;
            ThreadObject.RecountThreads();
            ThreadName.RecountThreads();
            LoggerObject.RecountLoggers();

            // The above calls may have added or removed a class of filtering.  Set the
            // butons, menus, and column header images accordingly.
            FilterAddedOrRemoved(null, null);

            if (_records.Count == 0) {
                MessageBox.Show(this, "There are no records in the file.", "TracerX");
            } else {
                ZeroTime = _records[0].Time;
                RebuildAllRows();

                // Attempt to maintain the same scroll position as before the refresh.
                if (_rowNumToRestore == -1 || _rowNumToRestore >= NumRows) {
                    // Go to the very end and select the last row so the next iteration will also
                    // scroll to the end.
                    SelectSingleRow(NumRows - 1);
                } else {
                    // Scroll to the same index as before the refresh.
                    // For some reason, setting the TopItem once doesn't work.  Setting
                    // it three times usually does, so try up to four.
                    for (int i = 0; i < 4; ++i) {
                        if (TheListView.TopItem.Index == _rowNumToRestore) break;
                        TheListView.TopItem = (TheListView.Items[_rowNumToRestore]);
                    }
                }
            }

            if (stopAutoRefresh.Enabled) {
                // Auto-refresh is on.  Set the timer.
                _refreshTimer.Interval = Settings.Default.AutoRefreshInterval * 1000;
                _refreshTimer.Start();
            }
        }

        private static void ResetFilters() {
            ThreadObject.AllThreads = new List<ThreadObject>();
            ThreadName.AllThreadNames = new List<ThreadName>();
            LoggerObject.AllLoggers = new List<LoggerObject>();
        }

        void _refreshTimer_Tick(object sender, EventArgs e) {
            if (_FileChanged) {
                StartReading(null); // Null means refresh the current file.
            }
        }

        private void backgroundWorker1_ProgressChanged(object sender, ProgressChangedEventArgs e) {
            int percent = Math.Min(100, e.ProgressPercentage);
            toolStripProgressBar1.Value = e.ProgressPercentage;
            pctLabel.Text = e.ProgressPercentage.ToString() + "%";
        }
        #endregion

        // Increment or decrement the CollapsedDepth of every record between this one and 
        // the corresponding Exit record (for the same thread).
        private void ExpandCollapseMethod(Record methodEntryRecord) {
            short diff = (short)(methodEntryRecord.IsCollapsed ? -1 : 1);

            methodEntryRecord.IsCollapsed = !methodEntryRecord.IsCollapsed;

            for (int i = methodEntryRecord.Index + 1; i < _records.Count; ++i) {
                Record current = _records[i];
                if (current.ThreadId == methodEntryRecord.ThreadId) {
                    // Current record is from the same thread as the clicked record.
                    if (current.StackDepth == methodEntryRecord.StackDepth) {
                        // This is the Exit record.
                        break;
                    } else {
                        current.CollapsedDepth += diff;
                    }
                }
            }
        }

        private void expandAllButton_Click(object sender, EventArgs e) {
            foreach (Record rec in _records) {
                rec.IsCollapsed = false;
                rec.CollapsedDepth = 0;
            }

            RebuildAllRows();
        }

        // Reset the _rows elements from startRow forward.
        // The specified Record (rec) is the first Record whose
        // visibility may need recalculating.  The specified
        // startRow will be mapped to the first visible Record found.
        private void RebuildRows(int startRow, Record rec) {
            Debug.Print("RebuildRows entered");

            // Display the wait cursor while we process the rows.
            Cursor restoreCursor = this.Cursor;
            this.Cursor = Cursors.WaitCursor;

            // Try to restore the scroll position when we're finished processing.
            Record showRec = null; // The record that has the focus.
            int showLine = 0;      // The line within the record that has the focus.
            int offset = 0;        // The offset of the focused row from the top row.
            if (TheListView.TopItem != null && FocusedRow != null) {
                showRec = FocusedRow.Rec;
                showLine = FocusedRow.Line;
                offset = FocusedRow.Index - TheListView.TopItem.Index;
            }

            int curRow = startRow;
            int curRec = rec.Index;

            while (curRec < _records.Count) {
                curRow = _records[curRec].SetVisibleRows(_rows, curRow);
                ++curRec;
            }

            ClearItemCache();
            NumRows = curRow;

            if (showRec != null) {
                int showRow = showRec.RowIndices[showLine];

                // If showRec is no longer visible, find the nearest record that is.
                while (!showRec.IsVisible && showRec.Index > 0) {
                    showRec = _records[showRec.Index - 1];
                }

                // If we found a visible record, scroll to it.
                if (showRec.IsVisible) {
                    if (showRow == -1 || !showRec.HasNewlines || showRec.IsCollapsed)
                        showRow = showRec.FirstRowIndex;

                    //Debug.Print("selecting item " + showRow);
                    SelectSingleRow(showRow);
                    int top = showRow - offset;
                    if (top > 0) {
                        //Debug.Print("setting top " + top);
                        TheListView.TopItem = TheListView.Items[top];
                    }
                }
            }

            this.Cursor = restoreCursor;
            Debug.Print("RebuildRows exiting");
        }

        protected override void OnClosing(CancelEventArgs e) {
            // Persist column widths in Settings.
            int[] widths = new int[OriginalColumns.Length];
            int[] indices = new int[OriginalColumns.Length];
            bool[] selections = new bool[OriginalColumns.Length];
            for (int i=0; i<OriginalColumns.Length; ++i) {
                widths[i] = OriginalColumns[i].Width;
                indices[i] = OriginalColumns[i].DisplayIndex;
                selections[i] = TheListView.Columns.Contains(OriginalColumns[i]);
            }
            Settings.Default.ColWidths = widths;
            Settings.Default.ColIndices = indices;
            Settings.Default.ColSelections = selections;
            Settings.Default.Save();

            base.OnClosing(e);
        }

        private ViewItem GetListItem(int i, out bool newItem) {
            // If we have the item cached, return it. Otherwise, recreate it.
            if (_itemCache != null &&
                i >= _firstItemIndex &&
                i < _firstItemIndex + _itemCache.Length) {
                //Debug.Print("Returning cached item " + i);
                newItem = false;
                return _itemCache[i - _firstItemIndex];
            } else {
                // Create a new item.
                newItem = true;

                if (i == 0) {
                    return _rows[i].MakeItem(null);
                } else {
                    return _rows[i].MakeItem(_rows[i - 1]);
                }
            }
        }

        private void listView1_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e) {
            // Only recreate the cache if we need to.
            if (_itemCache != null) {
                if (e.StartIndex >= _firstItemIndex &&
                    e.EndIndex <= _firstItemIndex + _itemCache.Length) {
                    return;
                }
            }

            Debug.Print("Building item cache " + e.StartIndex + " - " + e.EndIndex);
            bool newItem;
            ViewItem[] newCache = new ViewItem[e.EndIndex - e.StartIndex + 1];
            for (int i = 0; i < newCache.Length; i++) {
                // This will copy items from the old cache if it overlaps the new one.
                newCache[i] = GetListItem(e.StartIndex + i, out newItem);
            }

            _firstItemIndex = e.StartIndex;
            _itemCache = newCache;
        }

        private void ExecuteOpenFile(object sender, EventArgs e) {
            string openDir = Settings.Default.OpenDir;
            OpenFileDialog dlg = new OpenFileDialog();

            if (openDir != null && openDir != string.Empty && Directory.Exists(openDir))
                dlg.InitialDirectory = openDir;
            else
                dlg.InitialDirectory = Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            
            dlg.AddExtension = true;
            dlg.DefaultExt = ".tx1";
            dlg.Filter = "TracerX files (*.tx1)|*.tx1|All files (*.*)|*.*";
            dlg.Multiselect = false;
            dlg.Title = Application.ProductName;

            if (DialogResult.OK == dlg.ShowDialog()) {
                Settings.Default.OpenDir = Path.GetDirectoryName(dlg.FileName);
                StartReading(dlg.FileName);
            }
       }

        private void closeToolStripMenuItem_Click(object sender, EventArgs e) {
            _FileState = FileState.NoFile;
        }

        private void toolStripDropDownButton1_Click(object sender, EventArgs e) {
            backgroundWorker1.CancelAsync();
        }

        private void TheListView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo  hitTestInfo = TheListView.HitTest(e.X, e.Y);
            if (hitTestInfo != null && hitTestInfo.Item != null)
            {
                DateTime start = DateTime.Now;
                ViewItem item = hitTestInfo.Item as ViewItem; 
                Row row = item.Row;
                Record record = row.Rec;

                this.TheListView.BeginUpdate();

                if (record.IsEntry)
                {
                    // Expand or collapse the method call.
                    ExpandCollapseMethod(record);
                    RebuildRows(row.Index, row.Rec);
                }
                else if (record.HasNewlines && row.Index == record.FirstRowIndex)
                {
                    // Expand or collapse the record with embedded newlines.
                    record.IsCollapsed = !record.IsCollapsed;
                    RebuildRows(row.Index, row.Rec);
                }
                else
                {
                    // Display the record text in a window.
                    row.ShowFullText();
                }

                this.TheListView.EndUpdate();
                //Debug.Print("Double click handled, rows = " + NumRows + ", time = " + (DateTime.Now - start));
            }

        }

        private void ExecuteProperties(object sender, EventArgs e) {
            FileProperties dlg = new FileProperties(_reader);
            dlg.ShowDialog();
        }

        #region Bookmarks
        private void ExecuteToggleBookmark(object sender, EventArgs e) {
            if (TheListView.FocusedItem != null) {
                Row row = TheListView.FocusedItem.Tag as Row;
                row.IsBookmarked = !row.IsBookmarked;

                // The first bookmark created enables these commands.
                // They remain enabled until the file is unloaded or all
                // bookmarks are cleared.
                bookmarkPrevCmd.Enabled = true;
                bookmarkNextCmd.Enabled = true;
                bookmarkClearCmd.Enabled = true;

                // row.ImageIndex is determined by IsBookmarked.
                TheListView.FocusedItem.ImageIndex = row.ImageIndex;
                TheListView.Invalidate(TheListView.FocusedItem.GetBounds(ItemBoundsPortion.Icon));
            }
        }

        private void ExecuteClearBookmarks(object sender, EventArgs e) {
            // We must visit every record, including those that are collapsed/hidden.
            foreach (Record rec in _records) {
                for (int i = 0; i < rec.IsBookmarked.Length; ++i) {
                    rec.IsBookmarked[i] = false;
                }
            }

            bookmarkPrevCmd.Enabled = false;
            bookmarkNextCmd.Enabled = false;
            bookmarkClearCmd.Enabled = false;

            InvalidateTheListView();
        }

        // Search for a bookmarked row from start to just before end.
        private bool FindBookmark(int start, int end) {
            int moveBy = (start < end) ? 1 : -1;
            for (int i = start; i != end; i += moveBy) {
                if (_rows[i].IsBookmarked) {
                    SelectSingleRow(i);
                    
                    // Commented out old behavior that just focused the row, but did
                    // not select it.
                    //TheListView.EnsureVisible(i);
                    //if (TheListView.FocusedItem != null) TheListView.FocusedItem.Focused = false;
                    //TheListView.Items[i].Focused = true;
                    
                    return true;
                }
            }

            return false;
        }

        private void ExecuteNextBookmark(object sender, EventArgs e) {
            int start = 0;

            if (TheListView.FocusedItem != null) {
                start = TheListView.FocusedItem.Index + 1;
            }

            if (!FindBookmark(start, NumRows)) {
                // Wrap back to the first row.
                FindBookmark(0, start);
            }
        }

        private void ExecutePrevBookmark(object sender, EventArgs e) {
            int start = NumRows - 1;

            if (TheListView.FocusedItem != null) {
                start = TheListView.FocusedItem.Index - 1;
            }

            if (!FindBookmark(start, -1)) {
                // Wrap back to the last row.
                FindBookmark(NumRows - 1, start);
            }
        }
        #endregion Bookmarks

        // Display the Find dialog.
        private void ExecuteFind(object sender, EventArgs e) {
            FindDialog dlg = new FindDialog(this);
            dlg.Show(this);
        }

        // Search down for the current search string.
        private void ExecuteFindNext(object sender, EventArgs e) {
            if (_textMatcher != null) DoSearch(_textMatcher, false, false);
        }

        // Search up for the current search string.
        private void ExecuteFindPrevious(object sender, EventArgs e) {
            if (_textMatcher != null) DoSearch(_textMatcher, true, false);
        }

        // Clear all filtering.
        private void ExecuteClearFilter(object sender, EventArgs e) {
            ThreadObject.ShowAllThreads();
            ThreadName.ShowAllThreads();
            LoggerObject.ShowAllLoggers();
            VisibleTraceLevels = _reader.LevelsFound;
            FilterDialog.TextFilterDisable();
            RebuildAllRows();
        }

        // Called when the first filter is added or the last filter is
        // removed for a class of objects such as loggers or threads,
        // or whenever the presence/status of the filter icons that appear
        // in the column headers and the "clear all filtering" commands 
        // may need to be updated.
        private void FilterAddedOrRemoved(object sender, EventArgs e) {
            filterClearCmd.Enabled = false;

            if (VisibleTraceLevels == ValidTraceLevels) {
                headerLevel.ImageIndex = -1;
            } else {
                headerLevel.ImageIndex = 9;
                filterClearCmd.Enabled = true;
            }

            if (ThreadName.AllVisible) {
                headerThreadName.ImageIndex = -1;
            } else {
                // Show a filter in the header so user knows a thread filter is applies.
                headerThreadName.ImageIndex = 9;
                filterClearCmd.Enabled = true;
            }

            if (ThreadObject.AllVisible) {
                headerThreadId.ImageIndex = -1;
            } else {
                // Show a filter in the header so user knows a thread filter is applies.
                headerThreadId.ImageIndex = 9;
                filterClearCmd.Enabled = true;
            }

            if (LoggerObject.AllVisible) {
                headerLogger.ImageIndex = -1;
            } else {
                // Show a filter in the header so user knows a thread filter is applies.
                headerLogger.ImageIndex = 9;
                filterClearCmd.Enabled = true;
            }

            if (FilterDialog.TextFilterOn) {
                // Show a filter in the header so user knows a thread filter is applies.
                headerText.ImageIndex = 9;
                filterClearCmd.Enabled = true;
            } else {
                headerText.ImageIndex = -1;
            }
        }

        // Hide selected thread names
        private void hideSelectedThreadNamesMenuItem_Click(object sender, EventArgs e) {
            foreach (int index in TheListView.SelectedIndices) {
                _rows[index].Rec.ThreadName.Visible = false;
            }

            RebuildAllRows();

        }

        // Show only selected thread names
        private void showSelectedThreadNamesMenuItem_Click(object sender, EventArgs e) {
            ThreadName.HideAllThreads();

            foreach (int index in TheListView.SelectedIndices) {
                _rows[index].Rec.ThreadName.Visible = true;
            }

            RebuildAllRows();
        }

        // Hide selected thread IDs.
        private void hideSelectedThreadsMenuItem_Click(object sender, EventArgs e) {
            foreach (int index in TheListView.SelectedIndices) {
                _rows[index].Rec.Thread.Visible = false;
            }

            RebuildAllRows();
        }

        // Hide all but the selected thread IDs.
        private void showSelectedThreadsMenuItem_Click(object sender, EventArgs e) {
            ThreadObject.HideAllThreads();

            foreach (int index in TheListView.SelectedIndices) {
                _rows[index].Rec.Thread.Visible = true;
            }

            RebuildAllRows();
        }

        // Show the column selection dialog.
        private void ExecuteColumns(object sender, EventArgs e) {
            ColumnsDlg dlg = new ColumnsDlg(this);
            dlg.ShowDialog(this);
        }

        private void HideSelectedLoggersMenuItem_Click(object sender, EventArgs e) {
            foreach (int index in TheListView.SelectedIndices) {
                _rows[index].Rec.Logger.Visible = false;
            }

            RebuildAllRows();

        }

        private void ShowSelectedLoggersMenuItem_Click(object sender, EventArgs e) {
            LoggerObject.HideAllLoggers();

            foreach (int index in TheListView.SelectedIndices) {
                _rows[index].Rec.Logger.Visible = true;
            }

            RebuildAllRows();

        }

        // Hide rows with the same TraceLevel as the selected rows.
        private void HideTraceLevelsMenuItem_Click(object sender, EventArgs e) {
            TraceLevel newSetting = VisibleTraceLevels;

            foreach (int index in TheListView.SelectedIndices) {
                newSetting &= ~(_rows[index].Rec.Level);
            }

            VisibleTraceLevels = newSetting;
            RebuildAllRows();
       }                  


        // Show only the rows that have the same TraceLevel as the selected rows.
        private void ShowTraceLevelsMenuItem_Click(object sender, EventArgs e) {
            TraceLevel newSetting = TraceLevel.Inherited; // I.e. none.

            foreach (int index in TheListView.SelectedIndices) {
                newSetting |= _rows[index].Rec.Level;
            }

            // It's possible the selected trace levels are already the only
            // ones visible, in which case do nothing.
            if (VisibleTraceLevels != newSetting) {
                // Some trace levels got removed.
                VisibleTraceLevels = newSetting;
                RebuildAllRows();
            }
        }

        // Display the call stack of the selected record.
        private void showCallStackMenuItem_Click(object sender, EventArgs e) {
            // Search for the callers (call stack) of the currently selected record by
            // looking at the StackDepth of preceding records from the same thread.
            // This was implemented before the Caller field was added to the Record class,
            // and works for file versions before 5.

            // This is the list of records that comprise the stack.
            List<Record> stack = new List<Record>();
            Row startRow = _rows[TheListView.SelectedIndices[0]];
            Cursor restoreCursor = this.Cursor;
            this.Cursor = Cursors.WaitCursor;

            try {
                Record curRec = FindCaller(startRow.Rec);

                while (curRec != null) {
                    stack.Add(curRec);
                    curRec = FindCaller(curRec);
                }
            } finally {
                this.Cursor = restoreCursor;
            }

            stack.Reverse();
            CallStack dlg = new CallStack(startRow, stack);
            dlg.ShowDialog(this);
        }

        private Record FindCaller(Record start) {
            // We are only interested in records whose stack depth is less than curDepth.
            int curDepth = start.StackDepth;

            if (start.IsExit) ++curDepth;

            for (int index = start.Index - 1; index > -1 && curDepth > 0; --index) {
                Record curRec = _records[index];
                if (curRec.Thread == start.Thread) {
                    if (curRec.StackDepth < curDepth) {
                        curDepth = curRec.StackDepth;
                        if (curRec.IsEntry) {
                            return curRec;
                        }
                    }
                }
            }

            // Did not find the caller.
            return null;
        }

        // Scroll to the caller of the current record if visible.
        private void callerMenuItem_Click(object sender, EventArgs e) {
            Row startRow = _rows[TheListView.SelectedIndices[0]];
            Record caller;
            Cursor restoreCursor = this.Cursor;
            this.Cursor = Cursors.WaitCursor;

            try {
                caller = FindCaller(startRow.Rec);
            } finally {
                this.Cursor = restoreCursor;
            }

            if (caller == null) {
                // The caller was possibly lost in circular wrapping.
                MessageBox.Show("The caller was not found.");
            } else if (caller.IsVisible) {
                // Scroll to and select the row/item for the caller record.
                this.SelectSingleRow(caller.FirstRowIndex);
            } else {
                MessageBox.Show("The caller was found, but is not visible due to filtering.");
            }
        }

        // Find the end of the current method call and scroll to it.
        private void endOfMethodMenuItem_Click(object sender, EventArgs e) {
            Record startRec = CurrentRow.Rec;
            Cursor restoreCursor = this.Cursor;
            this.Cursor = Cursors.WaitCursor;

            // If the current record is a MethodEntry record, the end of the method is the next
            // record at the same stack depth.  Otherwise, the end of the method is the next
            // record at a lesser stack depth.
            int triggerDepth = startRec.StackDepth;

            if (startRec.IsEntry) ++triggerDepth;

            try {
                for (int index = startRec.Index + 1; index < _records.Count; ++index) {
                    Record curRec = _records[index];
                    if (curRec.Thread == startRec.Thread) {
                        if (curRec.StackDepth < triggerDepth) {
                            if (curRec.IsVisible) {
                                this.SelectSingleRow(curRec.FirstRowIndex);
                            } else {
                                MessageBox.Show("The end of the method call was found, but is not visible due to filtering.");
                            }
                            return;
                        }
                    }
                }

                MessageBox.Show("The end of the method call was not found.");
            } finally {
                this.Cursor = restoreCursor;
            }
        }

        private void copyTextMenuItem_Click(object sender, EventArgs e) {
            StringBuilder builder = new StringBuilder();
            foreach (int index in TheListView.SelectedIndices) {
                ViewItem item = TheListView.Items[index] as ViewItem;
                builder.Append(item.Row.GetFullIndentedText());
                builder.Append("\n");
            }

            // Remove last newline.
            if (builder.Length > 0) {
                builder.Length = builder.Length - 1;
                Clipboard.SetText(builder.ToString());
            }
        }

        private void UpdateMenuItems() {
            int selectCount = TheListView.SelectedIndices.Count;
            Record currentRec = CurrentRow == null ? null : CurrentRow.Rec;

            Debug.Print("selectCount: " + selectCount);

            hideSelectedToolStripMenuItem.Enabled = selectCount > 0;
            showOnlySelectedToolStripMenuItem.Enabled = selectCount > 0;
            copyTextMenuItem.Enabled = selectCount > 0;
            copyColsMenuItem.Enabled = selectCount > 0;
            bookmarkSelectedMenuItem.Enabled = selectCount > 0;
            showCallStackMenuItem.Enabled = selectCount == 1;
            callerMenuItem.Enabled = selectCount == 1;
            endOfMethodMenuItem.Enabled = selectCount == 1;
            viewTextWindowToolStripMenuItem.Enabled = selectCount == 1;
            setZeroTimeToolStripMenuItem.Enabled = selectCount == 1;

            if (selectCount == 1) {
                if (currentRec.StackDepth == 0) {
                    showCallStackMenuItem.Enabled = currentRec.IsExit;
                    callerMenuItem.Enabled = currentRec.IsExit;
                    endOfMethodMenuItem.Enabled = currentRec.IsEntry;
                }
            }

            // Try to include the method name in the "Goto start of method" menu item.
            if (callerMenuItem.Enabled) {
                if (currentRec.IsEntry) {
                    // In file format 5, Record.Caller is a direct reference to the caller of the 
                    // current line.  If that's available, use it.  If not, don't take the time to
                    // search for the caller just to set the menu item text.

                    if (currentRec.Caller == null) {
                        callerMenuItem.Text = "Start of caller";
                    } else {
                        callerMenuItem.Text = "Start of caller: " + currentRec.Caller.MethodName;
                    }
                } else {
                    callerMenuItem.Text = "Start of method: " + currentRec.MethodName;
                }
            } else {
                callerMenuItem.Text = "Start of method";
            }

            // Try to include the method name in the "Goto end of method" menu item.
            if (endOfMethodMenuItem.Enabled) {
                if (currentRec.IsExit) {
                    // We're already at the end of the current method, so this command will actually
                    // scroll to the end of the caller.  Try to get the caller's name.

                    if (currentRec.Caller == null || currentRec.Caller.Caller == null) {
                        endOfMethodMenuItem.Text = "End of caller";
                    } else {
                        endOfMethodMenuItem.Text = "End of caller: " + currentRec.Caller.Caller.MethodName;
                    }
                } else {
                    endOfMethodMenuItem.Text = "End of method: " + currentRec.MethodName;
                }
            } else {
                endOfMethodMenuItem.Text = "End of method";
            }
        }

        // Enable or disable find, find next, find prev.  It is not sufficient to only do this when 
        // the Edit menu is opening because these commands also have shortcut keys and toolbar buttons.
        private void UpdateFindCommands() {
            findCmd.Enabled = _FileState == FileState.Loaded && NumRows > 0;
            findNextCmd.Enabled = findPrevCmd.Enabled = (findCmd.Enabled && _textMatcher != null);
        }

        private void editToolStripMenuItem_DropDownOpening(object sender, EventArgs e) {
            UpdateMenuItems();
        }

        private void contextMenuStrip1_Opening(object sender, CancelEventArgs e) {
            // This call indirectly calls EnumWindowCallBack which sets _headerRect
            // to the area occupied by the ListView's header bar.
            NativeMethods.EnumChildWindows(TheListView.Handle, new NativeMethods.EnumWinCallBack(EnumWindowCallBack), IntPtr.Zero);

            // If the mouse position is in the header bar, cancel the display
            // of the regular context menu and display the column header context menu instead.
            if (_headerRect.Contains(Control.MousePosition)) {
                e.Cancel = true;

                // The xoffset is how far the mouse is from the left edge of the header.
                int xoffset = Control.MousePosition.X - _headerRect.Left;

                // Iterate through the columns in the order they are displayed, adding up
                // their widths as we go.  When the sum exceeds the xoffset, we know the mouse
                // is on the current column. 
                int sum = 0;
                foreach (ColumnHeader col in OrderedHeaders) {
                    sum += col.Width;
                    if (sum > xoffset) {
                        ShowMenuForColumnHeader(col);
                        break;
                    }
                }
            } else {
                // Update the items in the default context menu and allow it to be displayed.
                UpdateMenuItems();
            }
        }

        // This should get called with the only child window of the listview,
        // which should be the header bar.
        private bool EnumWindowCallBack(IntPtr hwnd, IntPtr lParam) {
            // Determine the rectangle of the header bar and save it in a member variable.
            NativeMethods.RECT rct;

            if (!NativeMethods.GetWindowRect(hwnd, out rct)) {
                _headerRect = Rectangle.Empty;
            } else {
                _headerRect = new Rectangle(rct.Left, rct.Top, rct.Right - rct.Left, rct.Bottom - rct.Top);
            }
            return false; // Stop the enum
        }

        // Keep certain commands enabled while the menu is closed
        private void editToolStripMenuItem_DropDownClosed(object sender, EventArgs e) {
            copyTextMenuItem.Enabled = true;
            copyColsMenuItem.Enabled = true;
        }

        private void copyColsMenuItem_Click(object sender, EventArgs e) {
            StringBuilder builder = new StringBuilder();
            string[] fields = new string[TheListView.Columns.Count];

            foreach (int index in TheListView.SelectedIndices) {
                ListViewItem item = TheListView.Items[index];
                foreach (ColumnHeader hdr in TheListView.Columns) {
                    if (hdr == headerText) {
                        // This is a special case because the text 
                        // message might be truncated in the ListView.
                        fields[hdr.DisplayIndex] = _rows[index].GetFullIndentedText();
                    } else {
                        fields[hdr.DisplayIndex] = item.SubItems[hdr.Index].Text;
                    }
                }

                foreach (string str in fields) {
                    builder.Append(str);
                    builder.Append(", ");
                }

                // Replace the last ", " with a newline.
                builder.Length = builder.Length - 2;
                builder.Append("\n");
            }

            // Remove last newline.
            if (builder.Length > 0) {
                builder.Length = builder.Length - 1;
                Clipboard.SetText(builder.ToString());
            }

        }

        // Bookmark all records/rows associated with the selected threads.
        private void bookmarkThreadsMenuItem_Click(object sender, EventArgs e) {
            // First make a list of the selected threads.
            List<ThreadObject> threads = new List<ThreadObject>();
            foreach (int index in TheListView.SelectedIndices) {
                threads.Add(_rows[index].Rec.Thread);
            }

            // Now set the IsBookmarked flag for every line of every record whose
            // thread is in the list we just made.
            foreach (Record rec in _records) {
                if (threads.Contains(rec.Thread)) {
                    for (int i=0; i< rec.IsBookmarked.Length; ++i) {
                        rec.IsBookmarked[i] = true;
                    }
                }
            }

            // We're not hiding or showing anything, so don't call RebuildRows.
            // Just make sure any visible rows get their images redrawn.
            InvalidateTheListView();
        }

        private void bookmarkLoggersMenuItem_Click(object sender, EventArgs e) {
            // First make a list of the selected loggers.
            List<LoggerObject> loggers = new List<LoggerObject>();
            foreach (int index in TheListView.SelectedIndices) {
                loggers.Add(_rows[index].Rec.Logger);
            }

            // Now set the IsBookmarked flag for every line of every record whose
            // logger is in the list we just made.
            foreach (Record rec in _records) {
                if (loggers.Contains(rec.Logger)) {
                    for (int i = 0; i < rec.IsBookmarked.Length; ++i) {
                        rec.IsBookmarked[i] = true;
                    }
                }
            }

            // We're not hiding or showing anything, so don't call RebuildRows.
            // Just make sure any visible rows get their images redrawn.
            InvalidateTheListView();

        }

        private void bookmarkTraceLevelsMenuItem_Click(object sender, EventArgs e) {
            TraceLevel levels = TraceLevel.Inherited; // I.e. none.

            foreach (int index in TheListView.SelectedIndices) {
                levels |= _rows[index].Rec.Level;
            }

            // Now set the IsBookmarked flag for every line of every record whose
            // trace level is in the bitmask we just made.
            foreach (Record rec in _records) {
                if ((rec.Level & levels) != TraceLevel.Inherited) {
                    for (int i = 0; i < rec.IsBookmarked.Length; ++i) {
                        rec.IsBookmarked[i] = true;
                    }
                }
            }

            // We're not hiding or showing anything, so don't call RebuildRows.
            // Just make sure any visible rows get their images redrawn.
            InvalidateTheListView();
        }

        private void ExecuteRefresh(object sender, EventArgs e) {
            Debug.Print("Refresh");
            ReportCurrentRow();
            StartReading(null); // Null means refresh the current file.
        }

        private void ReportCurrentRow() {
            Debug.Print("Selected count = " + TheListView.SelectedIndices.Count);
            Debug.Print("FocusedItem index = " + (TheListView.FocusedItem == null ? "null" : TheListView.FocusedItem.Index.ToString()));
            Debug.Print("CurrentRow = " + (CurrentRow == null ? "null" : CurrentRow.Index.ToString()));
        }

        private void viewTextWindowToolStripMenuItem_Click(object sender, EventArgs e) {
            Row row = _rows[TheListView.SelectedIndices[0]];
            row.ShowFullText();
        }

        private void ExecuteOpenFilterDialog(object sender, EventArgs e) {
            FilterDialog dialog = new FilterDialog();
            dialog.ShowDialog(this);
        }

        private void ExecuteOptions(object sender, EventArgs e) {
            OptionsDialog dlg = new OptionsDialog();
            dlg.ShowDialog(this);
        }

        private void setZeroTimeToolStripMenuItem_Click(object sender, EventArgs e) {
            Row row = _rows[TheListView.SelectedIndices[0]];
            ZeroTime = row.Rec.Time;
            Settings.Default.RelativeTime = true;
        }

        #region Column header context menu
        // Called when a column header is left-clicked
        private void TheListView_ColumnClick(object sender, ColumnClickEventArgs e) {
            ColumnHeader header = TheListView.Columns[e.Column];
            ShowMenuForColumnHeader(header);
        }

        // Shows the context menu for the specified column header.
        private void ShowMenuForColumnHeader(ColumnHeader header) {
            //First, enable the appropriate menu items for the specified header.
            MaybeEnableRemoveFromFilter(header);
            MaybeEnableFilter(header);
            MaybeEnableOptions(header);

            // Set the context menu tag to the specified header so the handler for 
            // whatever command the user clicks can know the column.
            columnContextMenu.Tag = header;
            columnContextMenu.Show(Control.MousePosition);
        }

        private void MaybeEnableOptions(ColumnHeader header) {
            if (_reader == null) {
                this.colMenuOptionsItem.Enabled = false;
            } else if (header == this.headerLine ||
                       header == this.headerTime ||
                       header == this.headerText) //
            {
                this.colMenuOptionsItem.Enabled = true;
            } else {
                this.colMenuOptionsItem.Enabled = false;
            }
        }

        private void MaybeEnableFilter(ColumnHeader header) {
            if (_reader == null) {
                this.colMenuFilterItem.Enabled = false;
            } else if (header == this.headerLevel ||
                       header == this.headerLogger ||
                       header == this.headerThreadName ||
                       header == this.headerText ||
                       header == this.headerThreadId) //
            {
                this.colMenuFilterItem.Enabled = true;
            } else {
                this.colMenuFilterItem.Enabled = false;
            }
        }

        private void MaybeEnableRemoveFromFilter(ColumnHeader header) {
            colMenuRemoveItem.Enabled = false;

            if (header == this.headerThreadId && !ThreadObject.AllVisible) {
                colMenuRemoveItem.Enabled = true;
            }

            if (header == this.headerThreadName && !ThreadName.AllVisible) {
                colMenuRemoveItem.Enabled = true;
            }

            if (header == this.headerLogger && !LoggerObject.AllVisible) {
                colMenuRemoveItem.Enabled = true;
            }

            if (header == this.headerLevel && _reader != null &&
                ((VisibleTraceLevels & _reader.LevelsFound) != _reader.LevelsFound)) {
                colMenuRemoveItem.Enabled = true;
            }

            if (header == this.headerText && FilterDialog.TextFilterOn) {
                colMenuRemoveItem.Enabled = true;
            }
        }

        private void colMenuFilterItem_Click(object sender, EventArgs e) {
            // columnContextMenu.Tag tells us which column header was clicked to
            // display the column context menu.  Pass it to the filter dialog to
            // specify the initial tab page.
            ColumnHeader header = (ColumnHeader)columnContextMenu.Tag;
            FilterDialog dialog = new FilterDialog(header);
            dialog.ShowDialog(this);
        }

        private void colMenuRemoveItem_Click(object sender, EventArgs e) {
            ColumnHeader header = (ColumnHeader)columnContextMenu.Tag;

            if (header == headerLevel) {
                VisibleTraceLevels = _reader.LevelsFound;
                RebuildAllRows();
            } else if (header == headerThreadId
                ) {
                ThreadObject.ShowAllThreads();
                RebuildAllRows();
            } else if (header == headerThreadName) {
                ThreadName.ShowAllThreads();
                RebuildAllRows();
            } else if (header == headerLogger) {
                LoggerObject.ShowAllLoggers();
                RebuildAllRows();
            } else if (header == headerText) {
                FilterDialog.TextFilterDisable();
                RebuildAllRows();
            }
        }

        private void colMenuOptionsItem_Click(object sender, EventArgs e) {
            // columnContextMenu.Tag tells us which column header was clicked to
            // display the column context menu.
            ColumnHeader header = (ColumnHeader)columnContextMenu.Tag;
            OptionsDialog dialog = new OptionsDialog(header);
            dialog.ShowDialog(this);
        }
        #endregion Column header context menu

        #region Recently Viewed/Created
        // Add the file to the list of recently viewed files.
        private void AddFileToRecentlyViewed(string filename) {
            if (Settings.Default.MRU == null) Settings.Default.MRU = new System.Collections.Specialized.StringCollection();

            if (Settings.Default.MRU.Contains(filename)) {
                // Remove the file we just loaded from Settings.Default.MRU.
                // It will be re-added at the end (most recent).
                Settings.Default.MRU.Remove(filename);
            }

            while (Settings.Default.MRU.Count > 6) {
                // Remove the oldest file in the list.
                Settings.Default.MRU.RemoveAt(0);
            }

            // Add the file we just loaded to the position of the most recent file in the MRU list.
            Settings.Default.MRU.Add(filename);
            Settings.Default.Save();
        }

        // Handler for opening the File menu.  Currently just sets 
        // the "Recently Viewed" and "Recently Created" menus.
        private void fileToolStripMenuItem_DropDownOpening(object sender, EventArgs e) {
            FillRecentlyViewedMenu();
            FillRecentlyCreatedMenu();
        }

        // Populate the Recently Viewed menu from the Settings.Default.MRU collection.
        private void FillRecentlyViewedMenu() {
            recentlyViewedToolStripMenuItem.DropDownItems.Clear();

            if (Settings.Default.MRU == null || Settings.Default.MRU.Count == 0) {
                recentlyViewedToolStripMenuItem.Enabled = false;
            } else {
                recentlyViewedToolStripMenuItem.Enabled = true;

                // Add a menu item for each file in Settings.Default.MRU.
                // The most recently opened file appears at the end of Settings.Default.MRU and
                // at the beginning of the MRU section of the File menu.
                foreach (string recentFile in Settings.Default.MRU) {
                    ToolStripMenuItem item = new ToolStripMenuItem(recentFile);
                    item.Tag = recentFile;
                    recentlyViewedToolStripMenuItem.DropDownItems.Insert(0, item);
                }
            }
        }

        // Populate the Recently Created menu from the RecentlyCreated.txt file.
        private void FillRecentlyCreatedMenu() {
            recentlyCreatedToolStripMenuItem.DropDownItems.Clear();

            // Get the list of recently created files from RecentlyCreated.txt.
            // The logger modifies this file each time it opens a file.
            try {
                string listFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "TracerX\\RecentlyCreated.txt"
                    );
                string[] files = File.ReadAllLines(listFile);

                if (files.Length > 0) {
                    foreach (string file in files) {
                        ToolStripMenuItem item = new ToolStripMenuItem(file);
                        item.Tag = file;
                        recentlyCreatedToolStripMenuItem.DropDownItems.Add(item);
                    }
                }
            } catch (Exception) {
                // The file containing the list of recently created filenames
                // probably doesn't exist.
            }

            recentlyCreatedToolStripMenuItem.Enabled = (recentlyCreatedToolStripMenuItem.DropDownItems.Count > 0);
        }

        // This handles selecting from the MRU file lists.
        private void RecentMenu_DropDownItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            if (e.ClickedItem.Tag != null) {
                string filename = (string)e.ClickedItem.Tag;

                // Remove the file from the "recently viewed" list now.  It
                // will be added back to the top of the list when file is finished loading.
                if (Settings.Default.MRU != null) Settings.Default.MRU.Remove(filename);

                StartReading(filename);
            }
        }
        #endregion

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e) {
            About about = new About();
            about.ShowDialog(this);
        }

        // Turns on the auto-refresh feature.
        private void startAutoRefresh_Click(object sender, EventArgs e) {
            startAutoRefresh.Enabled = false;
            stopAutoRefresh.Enabled = true;

            if (_FileChanged) {
                StartReading(null);
            } else {
                _refreshTimer.Interval = Settings.Default.AutoRefreshInterval * 1000;
                _refreshTimer.Start();
            }
        }

        // Turns off the auto-refresh feature.
        private void stopAutoRefresh_Click(object sender, EventArgs e) {
            startAutoRefresh.Enabled = true;
            stopAutoRefresh.Enabled = false;
            _refreshTimer.Stop();
        }

        private void licenseToolStripMenuItem_Click(object sender, EventArgs e)
        {
            License dlg = new License();
            dlg.ShowDialog(this);
        }

        private void TheListView_SelectedIndexChanged(object sender, EventArgs e) {
            if (TheListView.FocusedItem == null) {
                Debug.Print("SelectedIndexChanged " + TheListView.SelectedIndices.Count );
            } else {
                Debug.Print("SelectedIndexChanged " + TheListView.SelectedIndices.Count + " " + TheListView.FocusedItem.Text);
            }

            crumbPanel1.BuildCrumbBar(CurrentRow, _records);
            bookmarkToggleCmd.Enabled = TheListView.FocusedItem != null;

            // When the main form is not active, we do our own highlighting of selected items.
            if (this != Form.ActiveForm) SetItemCacheColors(false);
        }

        private void TheListView_VirtualItemsSelectionRangeChanged(object sender, ListViewVirtualItemsSelectionRangeChangedEventArgs e) {
            if (TheListView.FocusedItem == null) {
                Debug.Print("VirtualItemsSelectionRangeChanged, count = " + TheListView.SelectedIndices.Count);
            } else {
                Debug.Print("VirtualItemsSelectionRangeChanged, count = " + TheListView.SelectedIndices.Count + " " + TheListView.FocusedItem.Text);
            }

            // When the main form is not active, we do our own highlighting of selected items.
            if (this != Form.ActiveForm) SetItemCacheColors(false);
        }

        // Set the backcolor and forecolor of items in the cache so selected items
        // remain prominent even when the form loses focus.  I tried just setting
        // HideSelection to false, but the items become gray instead of highlighted
        // and the gray is nearly invisible on some monitors.
        // Called when selection(s) change.
        private void SetItemCacheColors(bool formActive) {
            Debug.Print("formActive = " + formActive);
            if (_itemCache != null) {
                TheListView.BeginUpdate();
                foreach (ViewItem item in _itemCache) item.SetItemColors(formActive);
                TheListView.EndUpdate();
            }
        }

        void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e) {
            switch (e.PropertyName) {
                case "RelativeTime":
                    relativeTimeButton.Checked = Settings.Default.RelativeTime;
                    absoluteTimeButton.Checked = !Settings.Default.RelativeTime;
                    InvalidateTheListView();
                    break;
                case "ColoringEnabled":
                    enableColorsBtn.Checked = Settings.Default.ColoringEnabled;
                    disableColorsBtn.Checked = !Settings.Default.ColoringEnabled;
                    enableColoringMenu.Visible = !Settings.Default.ColoringEnabled;
                    disableColoringMenu.Visible = Settings.Default.ColoringEnabled;
                    InvalidateTheListView();
                    break;
            }
        }

        private void relativeTimeButton_Click(object sender, EventArgs e) {
            if (sender == relativeTimeButton && !relativeTimeButton.Checked ||
                sender == absoluteTimeButton && !absoluteTimeButton.Checked) //
            {
                Settings.Default.RelativeTime = !Settings.Default.RelativeTime;
            }
        }

        private void MainForm_Activated(object sender, EventArgs e) {
            Debug.Print("MainForm_Activated, " + TheListView.SelectedIndices.Count + " selected");
            SetItemCacheColors(true);
        }

        private void closeAllWindowsToolStripMenuItem_Click(object sender, EventArgs e) {
            List<Form> toClose = new List<Form>();

            foreach (Form form in Application.OpenForms) {
                if (form != this) toClose.Add(form);
            }

            foreach (Form form in toClose) form.Close();
        }

        private void viewToolStripMenuItem_DropDownOpening(object sender, EventArgs e) {
            closeAllWindowsToolStripMenuItem.Enabled = Application.OpenForms.Count > 1;
        }

        private void TheListView_Leave(object sender, EventArgs e) {
            // The crumbBar gets the focus whenever the user clicks one of its links (including
            // disabled links).  When this happens, the selected items in the TheListView
            // are no longer highlighted.  Return the focus to TheListView so the selected
            // rows remain highlighted.
            this.ActiveControl = TheListView;
        }

        private void coloringCmd_Execute(object sender, EventArgs e) {
            ColorRulesDialog.ShowModal();
              
        }

        private void enableColors_Execute(object sender, EventArgs e) {
            Settings.Default.ColoringEnabled = true;
        }

        private void disableColors_Execute(object sender, EventArgs e) {
            Settings.Default.ColoringEnabled = false;
        }

        private void exportToCSVToolStripMenuItem_Click(object sender, EventArgs e) {
            ExportCSVForm.ShowModal();
        }
    }
}