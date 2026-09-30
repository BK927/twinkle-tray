using Windows.Graphics;

namespace TwinkleTray.WinUI.Services;

// Screen pixels and the originating Win32 message time travel with the request.
// In particular, keyboard activation must not be positioned at the mouse cursor.
internal readonly record struct TrayActivation(RectInt32 Anchor, bool Keyboard, uint MessageTime, uint PointerGesture = 0);

// Only an icon press followed by an icon release belongs to the same activation.
// A new press or an outside release discards abandoned clicks without a timer.
internal sealed class TrayPointerGestureTracker
{
    private uint _sequence, _releasedGesture;
    internal uint CurrentPress { get; private set; }
    internal uint CurrentGesture => CurrentPress != 0 ? CurrentPress : _releasedGesture;
    internal bool HasPendingGesture => CurrentPress != 0 || _releasedGesture != 0;

    internal void Press(bool onIcon)
    {
        _releasedGesture = 0;
        CurrentPress = 0;
        if (!onIcon) return;
        _sequence = unchecked(_sequence + 1);
        if (_sequence == 0) _sequence = 1;
        CurrentPress = _sequence;
    }

    internal void Release(bool onIcon)
    {
        _releasedGesture = onIcon ? CurrentPress : 0;
        CurrentPress = 0;
    }

    internal uint ConsumeActivation()
    {
        uint result = _releasedGesture;
        _releasedGesture = 0;
        return result;
    }
}
