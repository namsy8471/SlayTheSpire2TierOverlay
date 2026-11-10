using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// One STA thread for every test that builds real WPF views. The App's resources (brushes, converters) belong to the
/// thread that loaded them, and test classes run in parallel, so all such tests run here, one after another.
/// </summary>
internal static class WpfThread
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    public static void Run(Action test)
    {
        Exception? failure = null;
        Ui.Value.Invoke(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            // Tests run with invariant globalization; WPF's default "en-US" language would need culture data (once per process).
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("")));
            if (Application.Current is null) new App().InitializeComponent(); // App.xaml resources (brushes, converters, font)
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
