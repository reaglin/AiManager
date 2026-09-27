using Eaglin.AiManager.Tests.WinForms;
using Eaglin.AiManager.Wpf;

namespace Eaglin.AiManager.Tests.Wpf;

/// <summary>1.4.1: "Model for &lt;app&gt;" in the WPF AI Settings (Gamify+ and Author+ use it).</summary>
public class WpfSettingsTests
{
    [Fact]
    public void View_offers_Model_for_the_app_and_saves_a_pick_or_clears_it()
    {
        using var t = new TempRoot();
        WinFormsTestSupport.Sta(() =>
        {
            // pack://application: URIs (the view's merged Styles.xaml) need the WPF Application type initialised.
            if (System.Windows.Application.Current is null)
                _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

            var hub = WinFormsTestSupport.Hub(t, app: "Gamify+");
            hub.SetAppModel(AiProviderType.Gemini, "models/gemini-2.5-pro");

            var view = new AiSettingsView(hub);
            Assert.True(view.ShowsAppModels);
            Assert.Null(view.AppModelText(AiProviderType.Claude));
            Assert.Equal("models/gemini-2.5-pro", view.AppModelText(AiProviderType.Gemini));

            var builtIn = AiProviderRouter.DefaultModels[AiProviderType.Claude];
            view.PickAppModel(AiProviderType.Claude, builtIn);
            view.PickAppModel(AiProviderType.Gemini, null);
            view.SaveForTests();

            var back = AiHub.Open("Gamify+", t.Path);
            Assert.Equal(builtIn, back.AppModel(AiProviderType.Claude));      // the built-in default, chosen, sticks
            Assert.Null(back.AppModel(AiProviderType.Gemini));
            Assert.Equal(builtIn, view.AppModelText(AiProviderType.Claude));  // reloaded after Save

            var standalone = new AiSettingsView(WinFormsTestSupport.Hub(t, app: AiHub.ManagerAppName));
            Assert.False(standalone.ShowsAppModels);
        });
    }
}
