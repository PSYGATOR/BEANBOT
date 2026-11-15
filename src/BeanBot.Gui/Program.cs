namespace BeanBot.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeDir) && Directory.Exists(exeDir))
        {
            try
            {
                Environment.CurrentDirectory = exeDir;
            }
            catch
            {
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
