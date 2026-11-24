using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]

internal static class ChineseLauncher
{
    const uint SnapshotProcesses = 0x00000002;
    const uint QueryLimitedInformation = 0x00001000;
    const uint GetText = 0x0000000D;
    const uint SetText = 0x0000000C;
    const uint AbortIfHung = 0x00000002;
    const uint RedrawInvalidate = 0x0001;
    const uint RedrawAllChildren = 0x0080;
    const uint RedrawUpdateNow = 0x0100;
    const uint MessageTimeoutMs = 100;
    const int TextBufferCapacity = 8192;
    static readonly TimeSpan TimedOutWindowRetryDelay = TimeSpan.FromMilliseconds(500);
    const uint ProcessVmOperation = 0x0008;
    const uint ProcessVmRead = 0x0010;
    const uint ProcessVmWrite = 0x0020;
    const uint MemoryCommit = 0x1000;
    const uint MemoryReserve = 0x2000;
    const uint MemoryRelease = 0x8000;
    const uint PageReadWrite = 0x04;
    const uint TabGetItemCount = 0x1304;
    const uint TabGetItemW = 0x133C;
    const uint TabSetItemW = 0x133D;
    const uint TabItemText = 0x0001;
    const int TabTextCapacity = 256;
    const uint ListViewGetItemCount = 0x1004;
    const uint ListViewGetHeader = 0x101F;
    const uint ListViewGetColumnW = 0x105F;
    const uint ListViewSetColumnW = 0x1060;
    const uint ListViewGetItemTextW = 0x1073;
    const uint ListViewSetItemTextW = 0x1074;
    const uint HeaderGetItemCount = 0x1200;
    const uint ListViewColumnText = 0x0004;
    const int ListViewTextCapacity = 2048;
    const int MaxListViewRows = 500;
    const int MaxListViewColumns = 32;
    const int ScanIntervalMs = 250;

    static readonly HashSet<string> LocalizedExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "launcher.exe", "streamer.exe", "configure.exe", "performance.exe", "calibrate.exe", "tray.exe"
    };

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var bin = Path.Combine(root, "bin");
            var launcher = Path.Combine(bin, "launcher.exe");
            var translationsPath = Path.Combine(root, "zh-CN.json");
            if (!File.Exists(launcher))
                throw new FileNotFoundException("bin\\launcher.exe was not found. Place ChineseLauncher.exe and zh-CN.json in the AirFrameXR folder.", launcher);
            if (!File.Exists(translationsPath))
                throw new FileNotFoundException("zh-CN.json was not found beside ChineseLauncher.exe.", translationsPath);

            var translations = LoadTranslations(translationsPath);
            var start = new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = JoinArguments(args),
                WorkingDirectory = bin,
                UseShellExecute = false
            };
            using (var process = Process.Start(start))
            {
                if (process == null)
                    throw new InvalidOperationException("Windows did not return a process for bin\\launcher.exe.");
                Application.Run(new TranslationContext(bin, process.Id, translations));
            }
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            MessageBox.Show(ex.Message, "AirFrameXR Chinese launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        catch (IOException ex)
        {
            MessageBox.Show("Could not read the translation file: " + ex.Message, "AirFrameXR Chinese launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show("The translation file is invalid: " + ex.Message, "AirFrameXR Chinese launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show("Could not start or translate AirFrameXR: " + ex.Message, "AirFrameXR Chinese launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show("Could not start AirFrameXR: " + ex.Message, "AirFrameXR Chinese launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static TranslationMap LoadTranslations(string path)
    {
        object parsed;
        try
        {
            parsed = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        var source = parsed as Dictionary<string, object>;
        if (source == null || source.Count == 0)
            throw new InvalidDataException("Expected a non-empty JSON object of English strings and Chinese translations.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            var value = item.Value as string;
            if (string.IsNullOrEmpty(item.Key) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Every key and translation must be a non-empty string.");
            result.Add(item.Key, value);
        }
        return new TranslationMap(result);
    }

    sealed class TranslationMap
    {
        static readonly Regex Placeholder = new Regex(@"\{(\d+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        readonly Dictionary<string, string> exact;
        readonly List<TranslationPattern> patterns = new List<TranslationPattern>();

        sealed class TranslationPattern
        {
            public Regex Matcher;
            public string Value;
            public int Specificity;
        }

        public TranslationMap(Dictionary<string, string> entries)
        {
            exact = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var placeholders = Placeholder.Matches(entry.Key);
                if (placeholders.Count == 0)
                {
                    exact.Add(entry.Key, entry.Value);
                    continue;
                }

                var expression = new StringBuilder("^");
                var offset = 0;
                foreach (Match placeholder in placeholders)
                {
                    expression.Append(Regex.Escape(entry.Key.Substring(offset, placeholder.Index - offset)));
                    expression.Append("(.*?)");
                    offset = placeholder.Index + placeholder.Length;
                }
                expression.Append(Regex.Escape(entry.Key.Substring(offset))).Append("$");
                patterns.Add(new TranslationPattern
                {
                    Matcher = new Regex(expression.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline),
                    Value = entry.Value,
                    Specificity = entry.Key.Length - placeholders.Count * 3
                });
            }
            patterns.Sort((a, b) => b.Specificity.CompareTo(a.Specificity));
        }

        public bool TryTranslate(string text, out string translated)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (exact.TryGetValue(text, out translated)) return true;
            const string DefaultMarker = "  Default: ";
            var defaultMarker = text.LastIndexOf(DefaultMarker, StringComparison.Ordinal);
            if (defaultMarker > 0 && text.EndsWith(".", StringComparison.Ordinal) &&
                exact.TryGetValue(text.Substring(0, defaultMarker), out translated))
            {
                var defaultValue = text.Substring(defaultMarker + DefaultMarker.Length, text.Length - defaultMarker - DefaultMarker.Length - 1);
                string translatedDefault;
                if (exact.TryGetValue(defaultValue, out translatedDefault)) defaultValue = translatedDefault;
                translated += "  默认值：" + defaultValue + "。";
                return true;
            }
            foreach (var pattern in patterns)
            {
                var match = pattern.Matcher.Match(text);
                if (!match.Success) continue;
                translated = Placeholder.Replace(pattern.Value, m =>
                {
                    var index = Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
                    if (index >= match.Groups.Count) return m.Value;
                    var value = match.Groups[index].Value;
                    string valueTranslation;
                    return exact.TryGetValue(value, out valueTranslation) ? valueTranslation : value;
                });
                return true;
            }
            translated = null;
            return false;
        }
    }

    static string JoinArguments(string[] args)
    {
        var result = new StringBuilder();
        foreach (var arg in args)
        {
            if (result.Length > 0) result.Append(' ');
            result.Append('"');
            var slashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
        }
        return result.ToString();
    }

    sealed class TranslationContext : ApplicationContext
    {
        readonly string bin;
        readonly int launcherPid;
        readonly string launcherPath;
        readonly TranslationMap translations;
        readonly Dictionary<IntPtr, DateTime> retryAfter = new Dictionary<IntPtr, DateTime>();
        readonly HashSet<IntPtr> translatedTabs = new HashSet<IntPtr>();
        readonly Dictionary<IntPtr, int> translatedListRows = new Dictionary<IntPtr, int>();
        readonly Dictionary<IntPtr, DateTime> listViewNextScan = new Dictionary<IntPtr, DateTime>();
        readonly Timer timer = new Timer { Interval = ScanIntervalMs };

        public TranslationContext(string bin, int launcherPid, TranslationMap translations)
        {
            this.bin = Path.GetFullPath(bin).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            this.launcherPid = launcherPid;
            launcherPath = Path.GetFullPath(Path.Combine(bin, "launcher.exe"));
            this.translations = translations;
            timer.Tick += Scan;
            timer.Start();
        }

        void Scan(object sender, EventArgs e)
        {
            try
            {
                var processes = ReadProcesses();
                var live = new HashSet<int>();
                var processNames = new Dictionary<int, string>();
                foreach (var process in processes)
                {
                    if (!LocalizedExecutables.Contains(process.Name)) continue;
                    var image = GetImagePath(process.Id);
                    if (image == null)
                    {
                        if (process.Id != launcherPid) continue;
                    }
                    else
                    {
                        var fullImagePath = Path.GetFullPath(image);
                        if (!String.Equals(Path.GetFileName(fullImagePath), process.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (process.Id == launcherPid)
                        {
                            if (!String.Equals(fullImagePath, launcherPath, StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        else if (!fullImagePath.StartsWith(bin, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    live.Add(process.Id);
                    processNames[process.Id] = process.Name;
                }

                if (live.Count == 0)
                {
                    timer.Stop();
                    ExitThread();
                    return;
                }

                var windows = FindWindows(live);
                var currentWindows = new HashSet<IntPtr>(windows);
                foreach (var handle in new List<IntPtr>(retryAfter.Keys))
                    if (!currentWindows.Contains(handle)) retryAfter.Remove(handle);
                translatedTabs.RemoveWhere(handle => !currentWindows.Contains(handle));
                foreach (var handle in new List<IntPtr>(translatedListRows.Keys))
                {
                    if (currentWindows.Contains(handle)) continue;
                    translatedListRows.Remove(handle);
                    listViewNextScan.Remove(handle);
                }

                var redrawTargets = new HashSet<IntPtr>();
                foreach (var handle in windows)
                {
                    DateTime retry;
                    if (retryAfter.TryGetValue(handle, out retry) && DateTime.UtcNow < retry) continue;

                    uint windowPid;
                    GetWindowThreadProcessId(handle, out windowPid);
                    if (IsTabControl(handle))
                    {
                        if (!translatedTabs.Contains(handle) && TranslateTabItems(handle, (int)windowPid, translations))
                        {
                            translatedTabs.Add(handle);
                            retryAfter.Remove(handle);
                        }
                        else retryAfter[handle] = DateTime.UtcNow + TimedOutWindowRetryDelay;
                        continue;
                    }

                    if (IsListView(handle))
                    {
                        int rowCount;
                        if (!TryGetListViewRowCount(handle, out rowCount))
                        {
                            retryAfter[handle] = DateTime.UtcNow + TimedOutWindowRetryDelay;
                        }
                        else
                        {
                            int previousCount;
                            DateTime nextScan;
                            if (!translatedListRows.TryGetValue(handle, out previousCount) ||
                                previousCount != rowCount ||
                                !listViewNextScan.TryGetValue(handle, out nextScan) ||
                                DateTime.UtcNow >= nextScan)
                            {
                                if (TranslateListView(handle, (int)windowPid, translations))
                                {
                                    translatedListRows[handle] = rowCount;
                                    listViewNextScan[handle] = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                                    retryAfter.Remove(handle);
                                }
                                else retryAfter[handle] = DateTime.UtcNow + TimedOutWindowRetryDelay;
                            }
                        }
                        continue;
                    }

                    var text = new StringBuilder(TextBufferCapacity);
                    IntPtr textResult;
                    if (SendMessageTimeout(handle, GetText, new IntPtr(text.Capacity), text, AbortIfHung, MessageTimeoutMs, out textResult) == IntPtr.Zero)
                    {
                        if (IsWindow(handle)) retryAfter[handle] = DateTime.UtcNow + TimedOutWindowRetryDelay;
                        else retryAfter.Remove(handle);
                        continue;
                    }

                    string translated;
                    var sourceText = text.ToString();
                    if (!translations.TryTranslate(sourceText, out translated)) continue;
                    IntPtr setResult;
                    if (SendMessageTimeout(handle, SetText, IntPtr.Zero, translated, AbortIfHung, MessageTimeoutMs, out setResult) == IntPtr.Zero)
                    {
                        if (IsWindow(handle)) retryAfter[handle] = DateTime.UtcNow + TimedOutWindowRetryDelay;
                        else retryAfter.Remove(handle);
                        continue;
                    }
                    var parent = GetParent(handle);
                    redrawTargets.Add(parent != IntPtr.Zero ? parent : handle);
                    // SendMessageTimeout's return value reports delivery; WM_SETTEXT's result is control-specific.
                    retryAfter.Remove(handle);
                }
                RedrawTranslatedControls(redrawTargets);
            }
            catch (Win32Exception ex)
            {
                StopWithError(ex);
            }
            catch (InvalidOperationException ex)
            {
                StopWithError(ex);
            }
            catch (InvalidDataException ex)
            {
                StopWithError(ex);
            }
        }

        static void RedrawTranslatedControls(HashSet<IntPtr> windows)
        {
            foreach (var window in windows)
            {
                if (!IsWindow(window)) continue;
                if (!RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, RedrawInvalidate | RedrawAllChildren | RedrawUpdateNow) && IsWindow(window))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not redraw a translated control or its children.");
            }
        }

        void StopWithError(Exception ex)
        {
            timer.Stop();
            MessageBox.Show("The translation monitor stopped: " + ex.Message, "AirFrameXR Chinese launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitThread();
        }
    }

    static bool IsTabControl(IntPtr window)
    {
        var name = new StringBuilder(128);
        var length = GetClassName(window, name, name.Capacity);
        return length > 0 && name.ToString().IndexOf("SysTabControl32", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool IsListView(IntPtr window)
    {
        var name = new StringBuilder(128);
        var length = GetClassName(window, name, name.Capacity);
        return length > 0 && name.ToString().IndexOf("SysListView32", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool TranslateTabItems(IntPtr window, int pid, TranslationMap translations)
    {
        var process = OpenProcess(QueryLimitedInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, (uint)pid);
        if (process == IntPtr.Zero) return false;

        IntPtr remoteItem = IntPtr.Zero;
        IntPtr remoteText = IntPtr.Zero;
        try
        {
            IntPtr countResult;
            if (SendMessageTimeout(window, TabGetItemCount, IntPtr.Zero, IntPtr.Zero, AbortIfHung, MessageTimeoutMs, out countResult) == IntPtr.Zero)
                return false;
            var count = countResult.ToInt32();
            if (count < 1 || count > MaxListViewColumns) return false;

            var item = new TabItem { Mask = TabItemText, TextMax = TabTextCapacity };
            remoteItem = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr((uint)Marshal.SizeOf(typeof(TabItem))), MemoryCommit | MemoryReserve, PageReadWrite);
            remoteText = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr(TabTextCapacity * 2u), MemoryCommit | MemoryReserve, PageReadWrite);
            if (remoteItem == IntPtr.Zero || remoteText == IntPtr.Zero) return false;
            item.Text = remoteText;

            for (int i = 0; i < count; i++)
            {
                if (!WriteRemote(process, remoteItem, StructureBytes(item)) || !WriteRemote(process, remoteText, new byte[TabTextCapacity * 2]))
                    return false;
                IntPtr getResult;
                if (SendMessageTimeout(window, TabGetItemW, new IntPtr(i), remoteItem, AbortIfHung, MessageTimeoutMs, out getResult) == IntPtr.Zero)
                    return false;
                if (getResult == IntPtr.Zero) continue;

                string source;
                if (!ReadRemoteText(process, remoteText, TabTextCapacity * 2, out source)) return false;
                string translated;
                if (!translations.TryTranslate(source, out translated)) continue;
                if (!WriteRemoteText(process, remoteText, TabTextCapacity * 2, translated)) return false;

                IntPtr setResult;
                if (SendMessageTimeout(window, TabSetItemW, new IntPtr(i), remoteItem, AbortIfHung, MessageTimeoutMs, out setResult) == IntPtr.Zero || setResult == IntPtr.Zero)
                    return false;
            }
            return true;
        }
        finally
        {
            if (remoteText != IntPtr.Zero) VirtualFreeEx(process, remoteText, UIntPtr.Zero, MemoryRelease);
            if (remoteItem != IntPtr.Zero) VirtualFreeEx(process, remoteItem, UIntPtr.Zero, MemoryRelease);
            CloseHandle(process);
        }
    }

    static bool TryGetListViewRowCount(IntPtr window, out int count)
    {
        IntPtr result;
        if (SendMessageTimeout(window, ListViewGetItemCount, IntPtr.Zero, IntPtr.Zero, AbortIfHung, MessageTimeoutMs, out result) == IntPtr.Zero)
        {
            count = 0;
            return false;
        }
        count = result.ToInt32();
        return count >= 0 && count <= MaxListViewRows;
    }

    static bool TranslateListView(IntPtr window, int pid, TranslationMap translations)
    {
        var process = OpenProcess(QueryLimitedInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, (uint)pid);
        if (process == IntPtr.Zero) return false;

        IntPtr remoteColumn = IntPtr.Zero;
        IntPtr remoteItem = IntPtr.Zero;
        IntPtr remoteText = IntPtr.Zero;
        try
        {
            IntPtr headerResult;
            if (SendMessageTimeout(window, ListViewGetHeader, IntPtr.Zero, IntPtr.Zero, AbortIfHung, MessageTimeoutMs, out headerResult) == IntPtr.Zero)
                return false;
            var columnCount = 1;
            if (headerResult != IntPtr.Zero)
            {
                IntPtr countResult;
                if (SendMessageTimeout(headerResult, HeaderGetItemCount, IntPtr.Zero, IntPtr.Zero, AbortIfHung, MessageTimeoutMs, out countResult) == IntPtr.Zero)
                    return false;
                columnCount = countResult.ToInt32();
            }
            if (columnCount < 1 || columnCount > MaxListViewColumns) return false;

            remoteColumn = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr((uint)Marshal.SizeOf(typeof(ListViewColumn))), MemoryCommit | MemoryReserve, PageReadWrite);
            remoteItem = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr((uint)Marshal.SizeOf(typeof(ListViewItem))), MemoryCommit | MemoryReserve, PageReadWrite);
            remoteText = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr(ListViewTextCapacity * 2u), MemoryCommit | MemoryReserve, PageReadWrite);
            if (remoteColumn == IntPtr.Zero || remoteItem == IntPtr.Zero || remoteText == IntPtr.Zero) return false;

            var textBufferSize = ListViewTextCapacity * 2;
            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var column = new ListViewColumn { Mask = ListViewColumnText, Text = remoteText, TextMax = ListViewTextCapacity };
                if (!WriteRemote(process, remoteColumn, StructureBytes(column)) || !WriteRemote(process, remoteText, new byte[textBufferSize]))
                    return false;
                IntPtr getResult;
                if (SendMessageTimeout(window, ListViewGetColumnW, new IntPtr(columnIndex), remoteColumn, AbortIfHung, MessageTimeoutMs, out getResult) == IntPtr.Zero)
                    return false;
                if (getResult == IntPtr.Zero) continue;

                string source;
                if (!ReadRemoteText(process, remoteText, textBufferSize, out source)) return false;
                string translated;
                if (!TryTranslateListViewText(source, translations, out translated)) continue;
                if (!WriteRemoteText(process, remoteText, textBufferSize, translated)) return false;
                if (!WriteRemote(process, remoteColumn, StructureBytes(column))) return false;
                IntPtr setResult;
                if (SendMessageTimeout(window, ListViewSetColumnW, new IntPtr(columnIndex), remoteColumn, AbortIfHung, MessageTimeoutMs, out setResult) == IntPtr.Zero || setResult == IntPtr.Zero)
                    return false;
            }

            int rowCount;
            if (!TryGetListViewRowCount(window, out rowCount)) return false;
            for (int row = 0; row < rowCount; row++)
            {
                for (int subItem = 0; subItem < columnCount; subItem++)
                {
                    var item = new ListViewItem
                    {
                        Item = row,
                        SubItem = subItem,
                        Text = remoteText,
                        TextMax = ListViewTextCapacity
                    };
                    if (!WriteRemote(process, remoteItem, StructureBytes(item)) || !WriteRemote(process, remoteText, new byte[textBufferSize]))
                        return false;
                    IntPtr getResult;
                    if (SendMessageTimeout(window, ListViewGetItemTextW, new IntPtr(row), remoteItem, AbortIfHung, MessageTimeoutMs, out getResult) == IntPtr.Zero)
                        return false;
                    if (getResult == IntPtr.Zero) continue;

                    string source;
                    if (!ReadRemoteText(process, remoteText, textBufferSize, out source)) return false;
                    string translated;
                    if (!TryTranslateListViewText(source, translations, out translated)) continue;
                    if (!WriteRemoteText(process, remoteText, textBufferSize, translated)) return false;
                    if (!WriteRemote(process, remoteItem, StructureBytes(item))) return false;
                    IntPtr setResult;
                    if (SendMessageTimeout(window, ListViewSetItemTextW, new IntPtr(row), remoteItem, AbortIfHung, MessageTimeoutMs, out setResult) == IntPtr.Zero || setResult == IntPtr.Zero)
                        return false;
                }
            }
            return true;
        }
        finally
        {
            if (remoteText != IntPtr.Zero) VirtualFreeEx(process, remoteText, UIntPtr.Zero, MemoryRelease);
            if (remoteItem != IntPtr.Zero) VirtualFreeEx(process, remoteItem, UIntPtr.Zero, MemoryRelease);
            if (remoteColumn != IntPtr.Zero) VirtualFreeEx(process, remoteColumn, UIntPtr.Zero, MemoryRelease);
            CloseHandle(process);
        }
    }

    static bool ReadRemoteText(IntPtr process, IntPtr address, int byteCapacity, out string text)
    {
        var buffer = new byte[byteCapacity];
        IntPtr bytesRead;
        if (!ReadProcessMemory(process, address, buffer, buffer.Length, out bytesRead) || bytesRead.ToInt64() < 2)
        {
            text = null;
            return false;
        }
        var terminator = 0;
        while (terminator + 1 < buffer.Length && (buffer[terminator] != 0 || buffer[terminator + 1] != 0))
            terminator += 2;
        text = Encoding.Unicode.GetString(buffer, 0, terminator);
        return true;
    }

    static bool TryTranslateListViewText(string source, TranslationMap translations, out string translated)
    {
        string[] statusMarkers = { "☑  ", "⚠  ", "☒  ", "➖  ", "▶  ", "☐  " };
        foreach (var marker in statusMarkers)
        {
            if (!source.StartsWith(marker, StringComparison.Ordinal)) continue;
            string itemText;
            if (!translations.TryTranslate(source.Substring(marker.Length), out itemText)) break;
            translated = marker + itemText;
            return true;
        }
        return translations.TryTranslate(source, out translated);
    }

    static bool WriteRemoteText(IntPtr process, IntPtr address, int byteCapacity, string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        return bytes.Length <= byteCapacity && WriteRemote(process, address, bytes);
    }

    static byte[] StructureBytes<T>(T value) where T : struct
    {
        var size = Marshal.SizeOf(typeof(T));
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, memory, false);
            var bytes = new byte[size];
            Marshal.Copy(memory, bytes, 0, size);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    static bool WriteRemote(IntPtr process, IntPtr address, byte[] bytes)
    {
        IntPtr written;
        return WriteProcessMemory(process, address, bytes, bytes.Length, out written) && written.ToInt64() == bytes.Length;
    }

    static List<ProcessInfo> ReadProcesses()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate running processes.");

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
            var result = new List<ProcessInfo>();
            if (!Process32First(snapshot, ref entry))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the process list.");
            do
            {
                result.Add(new ProcessInfo((int)entry.ProcessId, entry.ExeFile));
                entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
            } while (Process32Next(snapshot, ref entry));

            var error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error, "Could not finish reading the process list.");
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    static string GetImagePath(int pid)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    static List<IntPtr> FindWindows(HashSet<int> processIds)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, state) =>
        {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (processIds.Contains((int)pid))
            {
                windows.Add(window);
                EnumChildWindows(window, (child, unused) => { windows.Add(child); return true; }, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    sealed class ProcessInfo
    {
        public readonly int Id;
        public readonly string Name;
        public ProcessInfo(int id, string name) { Id = id; Name = name; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TabItem
    {
        public uint Mask;
        public uint State;
        public uint StateMask;
        public IntPtr Text;
        public int TextMax;
        public int Image;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ListViewColumn
    {
        public uint Mask;
        public int Format;
        public int Width;
        public IntPtr Text;
        public int TextMax;
        public int SubItem;
        public int Image;
        public int Order;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ListViewItem
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public IntPtr Text;
        public int TextMax;
        public int Image;
        public IntPtr Param;
    }

    delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder imageName, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr bytesWritten);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RedrawWindow(IntPtr window, IntPtr updateRect, IntPtr updateRegion, uint flags);
}
