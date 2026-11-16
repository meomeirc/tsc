// Compile-time compatibility for editor-only Windows dialog code that remains in the
// shared source tree. Android menu builds never display these dialogs.
namespace System.Windows.Forms;

public interface IWin32Window
{
    IntPtr Handle { get; }
}

public enum DialogResult { None, OK, Cancel, Yes, No }
public enum MessageBoxButtons { OK, OKCancel, YesNo, YesNoCancel, RetryCancel, AbortRetryIgnore }
public enum MessageBoxIcon { None, Error, Question, Warning, Information }
public enum TextDataFormat { Text, UnicodeText }
public enum Keys { CapsLock }

public class CommonDialog : IDisposable
{
    public virtual DialogResult ShowDialog() => DialogResult.Cancel;
    public virtual DialogResult ShowDialog(IWin32Window? owner) => DialogResult.Cancel;
    public virtual void Dispose() { }
}

public class Form : CommonDialog { }

public sealed class OpenFileDialog : CommonDialog
{
    public string Filter { get; set; } = string.Empty;
    public bool CheckFileExists { get; set; }
    public bool Multiselect { get; set; }
    public bool RestoreDirectory { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}

public sealed class SaveFileDialog : CommonDialog
{
    public string Filter { get; set; } = string.Empty;
    public string DefaultExt { get; set; } = string.Empty;
    public bool AddExtension { get; set; }
    public bool OverwritePrompt { get; set; }
    public bool RestoreDirectory { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public sealed class FolderBrowserDialog : CommonDialog
{
    public string Description { get; set; } = string.Empty;
    public bool UseDescriptionForTitle { get; set; }
    public bool ShowNewFolderButton { get; set; }
    public string SelectedPath { get; set; } = string.Empty;
}

public static class MessageBox
{
    public static DialogResult Show(IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        _ = owner; _ = text; _ = caption; _ = buttons; _ = icon;
        return DialogResult.Cancel;
    }
}

public static class Clipboard
{
    public static bool ContainsText() => false;
    public static string GetText() => string.Empty;
    public static void SetText(string text) => _ = text;
    public static void SetText(string text, TextDataFormat format) { _ = text; _ = format; }
}

public static class Control
{
    public static bool IsKeyLocked(Keys key) { _ = key; return false; }
}
