# Keyboard Key Guard v3

Features:
- Unlimited protected keys
- Add Key / Remove / Clear All
- Explicit Save button
- Saved keys are shown when the app starts
- Start with Windows option
- Enable/Disable filter
- Settings stored in `%APPDATA%\KeyboardKeyGuard\settings.json`

Build:
1. Install .NET 8 SDK.
2. Extract the ZIP.
3. Run `build.bat`.
4. Run `publish\KeyboardKeyGuard.exe`.

Workflow:
1. Click Add Key.
2. Press a key.
3. Repeat Add Key for as many keys as desired.
4. Click Save.
5. Click Enable.
6. Close/reopen the program: the saved key list remains visible.

The filter allows the first key-down event for each protected key and suppresses additional key-down events until that key is released.
