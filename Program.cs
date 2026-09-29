using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace KeyboardKeyGuard;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;

    // Chatter debounce time.
    // Increase this if a faulty key still occasionally double-types.
    private const int DebounceMilliseconds = 100;

    private readonly ListBox keyList;
    private readonly Label statusLabel;
    private readonly Button addButton;
    private readonly Button removeButton;
    private readonly Button clearButton;
    private readonly Button saveButton;
    private readonly Button enableButton;
    private readonly CheckBox startupCheck;
    private readonly NotifyIcon tray;

    private static readonly HashSet<int> protectedKeys = new();
    private static readonly HashSet<int> physicalKeysDown = new();

    // Last accepted key-down time for each protected key.
    private static readonly Dictionary<int, long> lastAcceptedDown =
        new();

    private static bool filterEnabled;
    private static IntPtr hookHandle;

    private static readonly NativeMethods.LowLevelKeyboardProc HookProc =
        HookCallback;

    public MainForm()
    {
        Text = "Keyboard Key Guard";

        // Larger window so all text is visible.
        ClientSize = new Size(520, 560);

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // ------------------------------------------------------------
        // TITLE
        // ------------------------------------------------------------

        var title = new Label
        {
            Text = "Keyboard Key Guard",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 18)
        };

        // ------------------------------------------------------------
        // DESCRIPTION
        // ------------------------------------------------------------

        var info = new Label
        {
            Text =
                "Select as many keys as you want to protect from duplicate " +
                "key-down events and keyboard chatter.",
            AutoSize = false,
            Size = new Size(470, 45),
            Location = new Point(25, 58)
        };

        // ------------------------------------------------------------
        // LIST LABEL
        // ------------------------------------------------------------

        var listLabel = new Label
        {
            Text = "Saved / selected keys:",
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(25, 105)
        };

        // ------------------------------------------------------------
        // KEY LIST
        // ------------------------------------------------------------

        keyList = new ListBox
        {
            Location = new Point(25, 133),
            Width = 470,
            Height = 170,
            HorizontalScrollbar = true
        };

        // ------------------------------------------------------------
        // ADD BUTTON
        // ------------------------------------------------------------

        addButton = new Button
        {
            Text = "Add Key",
            Width = 110,
            Height = 36,
            Location = new Point(25, 320)
        };

        addButton.Click += (_, _) => AddKey();

        // ------------------------------------------------------------
        // REMOVE BUTTON
        // ------------------------------------------------------------

        removeButton = new Button
        {
            Text = "Remove",
            Width = 110,
            Height = 36,
            Location = new Point(145, 320)
        };

        removeButton.Click += (_, _) => RemoveSelected();

        // ------------------------------------------------------------
        // CLEAR BUTTON
        // ------------------------------------------------------------

        clearButton = new Button
        {
            Text = "Clear All",
            Width = 110,
            Height = 36,
            Location = new Point(265, 320)
        };

        clearButton.Click += (_, _) => ClearAll();

        // ------------------------------------------------------------
        // SAVE BUTTON
        // ------------------------------------------------------------

        saveButton = new Button
        {
            Text = "Save & Hide",
            Width = 110,
            Height = 36,
            Location = new Point(385, 320)
        };

        saveButton.Click += (_, _) => SaveSettingsAndHide();

        // ------------------------------------------------------------
        // ENABLE BUTTON
        // ------------------------------------------------------------

        enableButton = new Button
        {
            Text = "Enable",
            Width = 125,
            Height = 38,
            Location = new Point(25, 375)
        };

        enableButton.Click += (_, _) =>
            SetEnabled(!filterEnabled);

        // ------------------------------------------------------------
        // START WITH WINDOWS
        // ------------------------------------------------------------

        startupCheck = new CheckBox
        {
            Text = "Start with Windows",
            AutoSize = true,
            Location = new Point(175, 386)
        };

        startupCheck.CheckedChanged += (_, _) =>
        {
            SettingsManager.SetStartWithWindows(
                startupCheck.Checked);
        };

        // ------------------------------------------------------------
        // STATUS
        // ------------------------------------------------------------

        statusLabel = new Label
        {
            Text = "Status: Disabled",
            AutoSize = false,
            Size = new Size(470, 65),
            Location = new Point(25, 435)
        };

        // ------------------------------------------------------------
        // ADD CONTROLS
        // ------------------------------------------------------------

        Controls.AddRange(new Control[]
        {
            title,
            info,
            listLabel,
            keyList,
            addButton,
            removeButton,
            clearButton,
            saveButton,
            enableButton,
            startupCheck,
            statusLabel
        });

        // ------------------------------------------------------------
        // SYSTEM TRAY
        // ------------------------------------------------------------

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Keyboard Key Guard"
        };

        var menu = new ContextMenuStrip();

        menu.Items.Add(
            "Open",
            null,
            (_, _) => ShowWindow());

        menu.Items.Add(
            "Enable / Disable",
            null,
            (_, _) => SetEnabled(!filterEnabled));

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(
            "Exit",
            null,
            (_, _) => ExitApplication());

        tray.ContextMenuStrip = menu;

        tray.DoubleClick += (_, _) =>
            ShowWindow();

        // ------------------------------------------------------------
        // LOAD SETTINGS
        // ------------------------------------------------------------

        Load += (_, _) => LoadSettings();

        // ------------------------------------------------------------
        // MINIMIZE/CLOSE TO TRAY
        // ------------------------------------------------------------

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    // ================================================================
    // SETTINGS
    // ================================================================

    private void LoadSettings()
    {
        var settings = SettingsManager.Load();

        keyList.Items.Clear();
        protectedKeys.Clear();
        physicalKeysDown.Clear();
        lastAcceptedDown.Clear();

        foreach (int key in settings.ProtectedKeys
                     .Distinct()
                     .OrderBy(x => x))
        {
            protectedKeys.Add(key);
            keyList.Items.Add(GetKeyName(key));
        }

        startupCheck.Checked = settings.StartWithWindows;

        UpdateUi();

        // Automatically enable when Windows startup is enabled.
        if (settings.StartWithWindows &&
            protectedKeys.Count > 0)
        {
            SetEnabled(true);
        }
    }

    // ================================================================
    // ADD KEY
    // ================================================================

    private void AddKey()
    {
        using var dialog = new KeyCaptureForm();

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        int key = dialog.SelectedKey;

        if (protectedKeys.Contains(key))
        {
            MessageBox.Show(
                $"{GetKeyName(key)} is already in the list.",
                "Keyboard Key Guard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        protectedKeys.Add(key);

        RefreshKeyList();

        // Make sure the user understands that changes are not
        // permanently stored until Save & Hide is pressed.
        statusLabel.Text =
            $"Status: {protectedKeys.Count} key(s) configured." +
            Environment.NewLine +
            "Press \"Save & Hide\" to save your changes.";
    }

    // ================================================================
    // REMOVE KEY
    // ================================================================

    private void RemoveSelected()
    {
        if (keyList.SelectedItem is not string name)
            return;

        int key = protectedKeys.FirstOrDefault(
            x => GetKeyName(x) == name);

        if (key != 0)
        {
            protectedKeys.Remove(key);
            physicalKeysDown.Remove(key);
            lastAcceptedDown.Remove(key);

            RefreshKeyList();
        }
    }

    // ================================================================
    // CLEAR ALL
    // ================================================================

    private void ClearAll()
    {
        protectedKeys.Clear();
        physicalKeysDown.Clear();
        lastAcceptedDown.Clear();

        RefreshKeyList();
    }

    // ================================================================
    // REFRESH LIST
    // ================================================================

    private void RefreshKeyList()
    {
        string? selected =
            keyList.SelectedItem as string;

        keyList.Items.Clear();

        foreach (int key in protectedKeys.OrderBy(x => x))
        {
            keyList.Items.Add(GetKeyName(key));
        }

        if (selected != null)
        {
            int index =
                keyList.Items.IndexOf(selected);

            if (index >= 0)
                keyList.SelectedIndex = index;
        }

        UpdateUi();
    }

    // ================================================================
    // SAVE
    // ================================================================

    private void SaveSettings()
    {
        SettingsManager.Save(
            protectedKeys.OrderBy(x => x).ToList(),
            startupCheck.Checked);

        physicalKeysDown.Clear();
        lastAcceptedDown.Clear();

        UpdateUi();

        statusLabel.Text =
            filterEnabled
                ? $"Status: ENABLED — {protectedKeys.Count} key(s) protected." +
                  Environment.NewLine +
                  "Settings saved."
                : $"Status: Disabled — {protectedKeys.Count} key(s) configured." +
                  Environment.NewLine +
                  "Settings saved.";
    }

    private void SaveSettingsAndHide()
    {
        SaveSettings();

        Hide();
    }

    // ================================================================
    // ENABLE / DISABLE
    // ================================================================

    private void SetEnabled(bool value)
    {
        if (value && protectedKeys.Count == 0)
        {
            MessageBox.Show(
                "Add at least one key first.",
                "Keyboard Key Guard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        filterEnabled = value;

        physicalKeysDown.Clear();
        lastAcceptedDown.Clear();

        if (filterEnabled)
            InstallHook();
        else
            RemoveHook();

        UpdateUi();
    }

    // ================================================================
    // UI STATUS
    // ================================================================

    private void UpdateUi()
    {
        enableButton.Text =
            filterEnabled ? "Disable" : "Enable";

        if (filterEnabled)
        {
            statusLabel.Text =
                $"Status: ENABLED — {protectedKeys.Count} key(s) protected." +
                Environment.NewLine +
                $"Chatter filter: {DebounceMilliseconds} ms";
        }
        else
        {
            statusLabel.Text =
                $"Status: Disabled — {protectedKeys.Count} key(s) configured." +
                Environment.NewLine +
                "Press Enable to activate protection.";
        }
    }

    // ================================================================
    // KEYBOARD HOOK
    // ================================================================

    private static void InstallHook()
    {
        if (hookHandle != IntPtr.Zero)
            return;

        using ProcessModule module =
            Process.GetCurrentProcess().MainModule!;

        hookHandle = NativeMethods.SetWindowsHookEx(
            WH_KEYBOARD_LL,
            HookProc,
            NativeMethods.GetModuleHandle(
                module.ModuleName),
            0);

        if (hookHandle == IntPtr.Zero)
        {
            filterEnabled = false;

            MessageBox.Show(
                "Could not install the keyboard hook.",
                "Keyboard Key Guard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void RemoveHook()
    {
        if (hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(
                hookHandle);

            hookHandle = IntPtr.Zero;
        }

        physicalKeysDown.Clear();
        lastAcceptedDown.Clear();
    }

    // ================================================================
    // KEYBOARD CALLBACK
    // ================================================================

    private static IntPtr HookCallback(
        int nCode,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (nCode == HC_ACTION &&
            filterEnabled)
        {
            int message = wParam.ToInt32();

            var data =
                Marshal.PtrToStructure<
                    NativeMethods.KBDLLHOOKSTRUCT>(
                    lParam);

            int key = (int)data.vkCode;

            if (protectedKeys.Contains(key))
            {
                bool isDown =
                    message == WM_KEYDOWN ||
                    message == WM_SYSKEYDOWN;

                bool isUp =
                    message == WM_KEYUP ||
                    message == WM_SYSKEYUP;

                // ----------------------------------------------------
                // KEY DOWN
                // ----------------------------------------------------

                if (isDown)
                {
                    long now =
                        Environment.TickCount64;

                    // Case 1:
                    // Windows reports another DOWN while the physical
                    // key is already considered down.
                    //
                    // This catches repeated DOWN events.
                    if (physicalKeysDown.Contains(key))
                    {
                        return (IntPtr)1;
                    }

                    // Case 2:
                    // Key went DOWN -> UP -> DOWN extremely quickly.
                    //
                    // This is the classic mechanical keyboard chatter
                    // pattern.
                    if (lastAcceptedDown.TryGetValue(
                            key,
                            out long lastTime))
                    {
                        long elapsed =
                            now - lastTime;

                        if (elapsed >= 0 &&
                            elapsed < DebounceMilliseconds)
                        {
                            // Treat it as chatter.
                            physicalKeysDown.Add(key);

                            return (IntPtr)1;
                        }
                    }

                    // Legitimate key-down.
                    physicalKeysDown.Add(key);
                    lastAcceptedDown[key] = now;
                }

                // ----------------------------------------------------
                // KEY UP
                // ----------------------------------------------------

                else if (isUp)
                {
                    physicalKeysDown.Remove(key);

                    // IMPORTANT:
                    // Do not remove lastAcceptedDown here.
                    //
                    // We need it after the UP event so that a very
                    // fast DOWN -> UP -> DOWN sequence can be detected
                    // as chatter.
                }
            }
        }

        return NativeMethods.CallNextHookEx(
            hookHandle,
            nCode,
            wParam,
            lParam);
    }

    // ================================================================
    // KEY NAME
    // ================================================================

    private static string GetKeyName(int vk)
    {
        return vk == 0
            ? "NONE"
            : ((Keys)vk).ToString();
    }

    // ================================================================
    // SHOW WINDOW
    // ================================================================

    private void ShowWindow()
    {
        Show();

        WindowState =
            FormWindowState.Normal;

        Activate();
    }

    // ================================================================
    // EXIT
    // ================================================================

    private void ExitApplication()
    {
        filterEnabled = false;

        RemoveHook();

        tray.Visible = false;
        tray.Dispose();

        Application.Exit();
    }

    // ================================================================
    // DISPOSE
    // ================================================================

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            filterEnabled = false;

            RemoveHook();

            tray?.Dispose();
        }

        base.Dispose(disposing);
    }
}

// ====================================================================
// KEY CAPTURE WINDOW
// ====================================================================

internal sealed class KeyCaptureForm : Form
{
    public int SelectedKey { get; private set; }

    public KeyCaptureForm()
    {
        Text = "Add Key";

        ClientSize = new Size(400, 175);

        FormBorderStyle =
            FormBorderStyle.FixedDialog;

        MaximizeBox = false;
        MinimizeBox = false;

        StartPosition =
            FormStartPosition.CenterParent;

        KeyPreview = true;

        Controls.Add(new Label
        {
            Text =
                "Press the key you want to add to the protected list.",
            AutoSize = false,
            Size = new Size(350, 25),
            Location = new Point(25, 25)
        });

        Controls.Add(new Label
        {
            Text =
                "Press one key only. The window will close automatically.",
            AutoSize = false,
            Size = new Size(350, 25),
            Location = new Point(25, 52)
        });

        var cancel = new Button
        {
            Text = "Cancel",
            Width = 90,
            Height = 32,
            Location = new Point(150, 90)
        };

        cancel.Click += (_, _) =>
        {
            DialogResult =
                DialogResult.Cancel;

            Close();
        };

        Controls.Add(cancel);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode is
                Keys.ShiftKey or
                Keys.ControlKey or
                Keys.Menu or
                Keys.LWin or
                Keys.RWin)
            {
                return;
            }

            SelectedKey =
                (int)e.KeyCode;

            DialogResult =
                DialogResult.OK;

            Close();
        };
    }
}

// ====================================================================
// SETTINGS
// ====================================================================

internal sealed class AppSettings
{
    public List<int> ProtectedKeys { get; set; } =
        new();

    public bool StartWithWindows { get; set; }
}

// ====================================================================
// SETTINGS MANAGER
// ====================================================================

internal static class SettingsManager
{
    private static readonly string DirectoryPath =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "KeyboardKeyGuard");

    private static readonly string FilePath =
        Path.Combine(
            DirectoryPath,
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            string json =
                File.ReadAllText(FilePath);

            return JsonSerializer.Deserialize<AppSettings>(
                       json)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(
        List<int> protectedKeys,
        bool startWithWindows)
    {
        try
        {
            Directory.CreateDirectory(
                DirectoryPath);

            var settings = new AppSettings
            {
                ProtectedKeys =
                    protectedKeys
                        .Distinct()
                        .ToList(),

                StartWithWindows =
                    startWithWindows
            };

            string json =
                JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

            File.WriteAllText(
                FilePath,
                json);
        }
        catch
        {
            // Ignore settings write errors.
        }
    }

    public static void SetStartWithWindows(
        bool enabled)
    {
        try
        {
            using var key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    writable: true);

            if (key == null)
                return;

            if (enabled)
            {
                string exe =
                    Application.ExecutablePath;

                key.SetValue(
                    "KeyboardKeyGuard",
                    $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(
                    "KeyboardKeyGuard",
                    throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore registry errors.
        }
    }
}

// ====================================================================
// NATIVE WINDOWS METHODS
// ====================================================================

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    internal delegate IntPtr LowLevelKeyboardProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(
        IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(
        string? lpModuleName);
}