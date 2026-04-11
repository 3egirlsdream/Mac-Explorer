namespace FKFinder.Services;

public interface IMouseNavigationService
{
    event Action? BackButtonPressed;
    event Action? ForwardButtonPressed;
    void Start();
    void Stop();
}
