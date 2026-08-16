using Argus.Server.Windows;

namespace Argus.Server.Input;

public interface IInputInjector
{
    string Name { get; }

    /// <summary>Whether this backend can drive the given window at all.</summary>
    bool CanHandle(WindowInfo target);

    /// <summary>Returns false when the key could not be delivered.</summary>
    bool TrySend(WindowInfo target, KeyEventDto keyEvent);
}
