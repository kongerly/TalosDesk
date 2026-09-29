using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class WpfTestHostTests
{
    [STATestMethod]
    public void TestHostProvidesStaDispatcherForDesktopControls()
    {
        Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        var control = new TextBlock { Text = "关于与更新" };

        Assert.AreSame(Dispatcher.CurrentDispatcher, control.Dispatcher);
        control.Dispatcher.VerifyAccess();
        Assert.AreEqual("关于与更新", control.Text);
    }
}
