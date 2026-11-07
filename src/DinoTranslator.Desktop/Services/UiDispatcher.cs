using System.Windows;

namespace DinoTranslator.Desktop.Services;

/// <summary>Leva o trabalho de outras threads (ex.: eventos do motor de fala) para a thread da janela.</summary>
public interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action) => Application.Current.Dispatcher.InvokeAsync(action).Task;
}
