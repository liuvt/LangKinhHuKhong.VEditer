using LangKinhHuKhong.Editer.UI;

namespace LangKinhHuKhong.Editer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
