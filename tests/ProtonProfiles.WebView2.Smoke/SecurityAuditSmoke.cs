using System.Reflection;
using System.Windows.Controls;
using ProtonProfiles.App.Dialogs;

internal static class SecurityAuditSmoke
{
    public static void Run()
    {
        var factory = typeof(ProfileDiagnosticsWindow).GetMethod("CreateGrid", BindingFlags.NonPublic | BindingFlags.Static)!;
        var grid = (DataGrid)factory.Invoke(null, null)!;
        var column = new DataGridTextColumn();
        grid.Columns.Add(column);
        var item = new object();
        var args = new DataGridRowClipboardEventArgs(item, 0, 0, false);
        args.ClipboardRowContent.Add(new(item, column, "+1+2"));
        args.ClipboardRowContent.Add(new(item, column, "GET"));
        args.ClipboardRowContent.Add(new(item, column, 200));
        // Exercise the production copy event without reading/writing the OS clipboard.
        typeof(DataGrid).GetMethod("OnCopyingRowClipboardContent", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(grid, [args]);
        if (!Equals(args.ClipboardRowContent[0].Content, "'+1+2")
            || !Equals(args.ClipboardRowContent[1].Content, "GET") || !Equals(args.ClipboardRowContent[2].Content, 200))
            throw new InvalidOperationException("Diagnostic clipboard rows must keep untrusted text literal and preserve ordinary values.");
        Console.WriteLine("PASS: production diagnostic clipboard copy escapes formulas; ordinary text/numbers unchanged; OS clipboard untouched.");
    }
}
