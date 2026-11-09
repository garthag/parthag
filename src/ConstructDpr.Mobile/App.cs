namespace ConstructDpr.Mobile;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(new MainPage()) { Title = "CONSTRUCT DPR" };
}
