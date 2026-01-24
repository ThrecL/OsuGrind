using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace OsuGrind.Services;

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> action;
    private bool isRunning;

    public RelayCommand(Func<Task> action) => this.action = action;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !isRunning;

    public async void Execute(object? parameter)
    {
        if (isRunning) return;
        try
        {
            isRunning = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await action();
        }
        finally
        {
            isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> action;
    private bool isRunning;

    public RelayCommand(Func<T?, Task> action) => this.action = action;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !isRunning;

    public async void Execute(object? parameter)
    {
        if (isRunning) return;
        try
        {
            isRunning = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);

            if (parameter is T t)
            {
                await action(t);
            }
            else if (parameter is string s && typeof(T) == typeof(int) && int.TryParse(s, out var i))
            {
                await action((T)(object)i);
            }
            else
            {
                await action(default);
            }
        }
        finally
        {
            isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
