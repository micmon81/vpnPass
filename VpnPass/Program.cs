namespace VpnPass;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, @"Local\vpnPass.Desktop", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("vpnPass is already running. Open it from the Windows notification area.", "vpnPass");
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "vpnPass", MessageBoxButtons.OK, MessageBoxIcon.Error);
        try { Application.Run(new MainForm(args.Contains("--preview"))); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "vpnPass could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
