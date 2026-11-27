using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;

[assembly: InternalsVisibleTo("DeskLink.Smoke")]
[assembly: AssemblyTitle("同桌传")]
[assembly: AssemblyDescription("两台电脑之间的消息与文件传输助手")]
[assembly: AssemblyProduct("同桌传")]
[assembly: AssemblyVersion("2.2.0.0")]

namespace DeskLink
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (MainWindow == null) new MainWindow().Show();
        }
    }
}
